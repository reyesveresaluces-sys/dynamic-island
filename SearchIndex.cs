using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DynamicIsland
{
    public enum ResultKind { App, File, Content }

    public sealed class SearchResult
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public ResultKind Kind { get; init; }
        public string Snippet { get; init; } = ""; // con marcadores \u0001 … \u0002 alrededor de las coincidencias
        public DateTime Modified { get; init; }
    }

    /// <summary>
    /// Índice de texto completo de los discos (nombres + contenido) en SQLite FTS5.
    /// 1) Rastreo rápido de nombres de todos los archivos.
    /// 2) Extracción de contenido en segundo plano (los más recientes primero).
    /// 3) FileSystemWatcher para cambios en vivo + re-rastreo periódico.
    /// </summary>
    public sealed class SearchIndex : IDisposable
    {
        public static readonly string DataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
        private static readonly string DbPath = System.IO.Path.Combine(DataDir, "index.db");

        public event Action? StatusChanged;
        public string Status { get; private set; } = "Preparando índice…";
        public long FileCount { get; private set; }
        public long PendingContent { get; private set; }
        public bool IsBusy { get; private set; }

        private readonly ConcurrentQueue<string> _changes = new();
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly AutoResetEvent _wake = new(false);
        private volatile bool _rescanRequested = true;
        private long _gen;
        private Thread? _worker;

        // Carpetas que no tiene sentido indexar (sistema, cachés, papelera…)
        private static readonly string[] SkipNames =
        {
            "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "$WinREAgent", "$SysReset",
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "PerfLogs", "MSOCache",
            "node_modules", ".git", ".svn", ".vs", ".gradle", ".nuget", "__pycache__", "AppData", "WindowsApps",
            "obj", "bin\\Debug", ".cache", "Temp"
        };

        private static readonly string[] AppRoots =
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };

        public void Start()
        {
            Directory.CreateDirectory(DataDir);
            using (var con = Open()) CreateSchema(con);
            _worker = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Indexador" };
            _worker.Start();
            StartWatchers();
        }

        public void RequestRescan()
        {
            _rescanRequested = true;
            _wake.Set();
        }

        /// <summary>Se activó o desactivó el OCR en los ajustes.</summary>
        public void OcrSettingChanged()
        {
            _ocrChanged = true;
            _wake.Set();
        }

        private volatile bool _ocrChanged = true; // al arrancar, revisar si falta encolar el OCR

        /// <summary>
        /// Encola imágenes y PDF escaneados que se indexaron antes de activar el OCR,
        /// o quita de la cola el trabajo de OCR si se desactivó.
        /// </summary>
        private void ApplyOcrSetting(SqliteConnection con)
        {
            _ocrChanged = false;
            bool enabled = AppSettings.Current.OcrEnabled;
            string queued;
            using (var get = con.CreateCommand())
            {
                get.CommandText = "SELECT v FROM meta WHERE k='ocr_queued'";
                queued = get.ExecuteScalar() as string ?? "0";
            }
            if (enabled == (queued == "1")) return;

            using var tx = con.BeginTransaction();
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            if (enabled)
            {
                SetStatus(true, "Preparando OCR…");
                cmd.CommandText = @"
UPDATE files SET pending = 2
 WHERE pending = 0 AND size BETWEEN $min AND $max
   AND ext IN ('.jpg','.jpeg','.png','.bmp','.tif','.tiff','.gif','.webp','.jfif','.heic');
UPDATE files SET pending = 1
 WHERE pending = 0 AND ext = '.pdf'
   AND id IN (SELECT rowid FROM docs WHERE length(body) < 200);
INSERT INTO meta(k,v) VALUES('ocr_queued','1') ON CONFLICT(k) DO UPDATE SET v='1';";
                cmd.Parameters.AddWithValue("$min", MinOcrImage);
                cmd.Parameters.AddWithValue("$max", MaxOcrImage);
            }
            else
            {
                cmd.CommandText = @"
UPDATE files SET pending = 0 WHERE pending = 2;
INSERT INTO meta(k,v) VALUES('ocr_queued','0') ON CONFLICT(k) DO UPDATE SET v='0';";
            }
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        private static SqliteConnection Open(bool readOnly = false)
        {
            var con = new SqliteConnection($"Data Source={DbPath};Mode={(readOnly ? "ReadOnly" : "ReadWriteCreate")};Cache=Private");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = readOnly
                ? "PRAGMA busy_timeout=3000;"
                : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
            return con;
        }

        private static void CreateSchema(SqliteConnection con)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS files(
    id INTEGER PRIMARY KEY,
    path TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    ext TEXT NOT NULL,
    size INTEGER NOT NULL,
    mtime INTEGER NOT NULL,
    gen INTEGER NOT NULL,
    pending INTEGER NOT NULL DEFAULT 0,
    isapp INTEGER NOT NULL DEFAULT 0);
DROP INDEX IF EXISTS ix_files_pending;
CREATE INDEX IF NOT EXISTS ix_files_todo ON files(pending, mtime) WHERE pending > 0;
CREATE INDEX IF NOT EXISTS ix_files_gen ON files(gen);
CREATE VIRTUAL TABLE IF NOT EXISTS docs USING fts5(name, body, tokenize = 'unicode61 remove_diacritics 2');
CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);";
            cmd.ExecuteNonQuery();
        }

        // ================= Hilo de indexado =================

        private void WorkerLoop()
        {
            var token = _cts.Token;
            DateTime lastScan = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var con = Open();
                    _gen = ReadGen(con);

                    if (_rescanRequested || DateTime.Now - lastScan > TimeSpan.FromHours(6))
                    {
                        _rescanRequested = false;
                        FullScan(con, token);
                        lastScan = DateTime.Now;
                    }

                    if (_ocrChanged) ApplyOcrSetting(con);
                    ProcessChanges(con);
                    bool more = ExtractBatch(con, token);
                    UpdateCounts(con);

                    if (!more && _changes.IsEmpty)
                    {
                        SetStatus(false, $"{FileCount:N0} archivos indexados");
                        _wake.WaitOne(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    SetStatus(false, "Índice: " + ex.Message);
                    _wake.WaitOne(TimeSpan.FromSeconds(30));
                }
            }
        }

        private static long ReadGen(SqliteConnection con)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT v FROM meta WHERE k='gen'";
            return cmd.ExecuteScalar() is string s && long.TryParse(s, out var g) ? g : 0;
        }

        private static void WriteGen(SqliteConnection con, long gen)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(k,v) VALUES('gen',$v) ON CONFLICT(k) DO UPDATE SET v=$v";
            cmd.Parameters.AddWithValue("$v", gen.ToString());
            cmd.ExecuteNonQuery();
        }

        private void FullScan(SqliteConnection con, CancellationToken token)
        {
            long gen = _gen + 1;
            var roots = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => d.RootDirectory.FullName).ToList();

            var batch = new Batch(con, gen);
            long seen = 0;

            foreach (var appRoot in AppRoots.Where(Directory.Exists))
                Crawl(appRoot, isApp: true, batch, ref seen, token);

            foreach (var root in roots)
                Crawl(root, isApp: false, batch, ref seen, token);

            batch.Commit();
            if (token.IsCancellationRequested) return;

            // Borrar lo que ya no existe
            using (var tx = con.BeginTransaction())
            using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM docs WHERE rowid IN (SELECT id FROM files WHERE gen < $g); DELETE FROM files WHERE gen < $g;";
                cmd.Parameters.AddWithValue("$g", gen);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            _gen = gen;
            WriteGen(con, gen);
        }

        private void Crawl(string root, bool isApp, Batch batch, ref long seen, CancellationToken token)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline
            };

            while (stack.Count > 0 && !token.IsCancellationRequested)
            {
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos("*", opts).ToList(); }
                catch { continue; }

                foreach (var e in entries)
                {
                    if (e is DirectoryInfo d)
                    {
                        if (!isApp && ShouldSkipDir(d)) continue;
                        stack.Push(d);
                    }
                    else if (e is FileInfo f)
                    {
                        if (isApp && !f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!isApp && f.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                        batch.Upsert(f, isApp);
                        if (++seen % 2000 == 0)
                        {
                            batch.Commit();
                            SetStatus(true, $"Indexando nombres… {seen:N0}");
                        }
                    }
                }
            }
        }

        private static bool ShouldSkipDir(DirectoryInfo d)
        {
            if (d.Attributes.HasFlag(FileAttributes.Hidden) && d.Parent?.Parent == null) return true; // ocultos en la raíz
            foreach (var s in SkipNames)
            {
                if (s.Contains('\\'))
                {
                    if (d.FullName.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (d.Name.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return d.FullName.StartsWith(DataDir, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipPath(string path)
        {
            var parts = path.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts.Take(parts.Length - 1))
                foreach (var s in SkipNames)
                    if (!s.Contains('\\') && p.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
            return path.StartsWith(DataDir, StringComparison.OrdinalIgnoreCase);
        }

        // Cola de lectura de contenido: 1 = documentos (rápido), 2 = OCR de imágenes (lento, va al final)
        private const long MinOcrImage = 10 * 1024, MaxOcrImage = 30L * 1024 * 1024;

        private static int PendingFor(string ext, long size, bool isApp)
        {
            if (isApp || size <= 0) return 0;
            if (TextExtractor.IsSupported(ext) && size <= TextExtractor.MaxSize(ext)) return 1;
            if (AppSettings.Current.OcrEnabled && TextExtractor.IsImage(ext) && size >= MinOcrImage && size <= MaxOcrImage) return 2;
            return 0;
        }

        /// <summary>Escrituras agrupadas en transacciones para que el rastreo sea rápido.</summary>
        private sealed class Batch
        {
            private readonly SqliteConnection _con;
            private readonly long _gen;
            private SqliteTransaction? _tx;

            public Batch(SqliteConnection con, long gen) { _con = con; _gen = gen; }

            public void Upsert(FileInfo f, bool isApp)
            {
                _tx ??= _con.BeginTransaction();
                string ext = f.Extension.ToLowerInvariant();
                long size, mtime;
                try { size = f.Length; mtime = f.LastWriteTimeUtc.Ticks; }
                catch { return; }

                int wantsContent = PendingFor(ext, size, isApp);
                string display = isApp ? System.IO.Path.GetFileNameWithoutExtension(f.Name) : f.Name;

                using var find = _con.CreateCommand();
                find.Transaction = _tx;
                find.CommandText = "SELECT id, size, mtime FROM files WHERE path = $p";
                find.Parameters.AddWithValue("$p", f.FullName);
                using var r = find.ExecuteReader();
                if (r.Read())
                {
                    long id = r.GetInt64(0);
                    bool changed = r.GetInt64(1) != size || r.GetInt64(2) != mtime;
                    r.Close();
                    using var up = _con.CreateCommand();
                    up.Transaction = _tx;
                    up.CommandText = changed
                        ? "UPDATE files SET size=$s, mtime=$m, gen=$g, pending=$pe, isapp=$a WHERE id=$id"
                        : "UPDATE files SET gen=$g WHERE id=$id";
                    up.Parameters.AddWithValue("$s", size);
                    up.Parameters.AddWithValue("$m", mtime);
                    up.Parameters.AddWithValue("$g", _gen);
                    up.Parameters.AddWithValue("$pe", wantsContent);
                    up.Parameters.AddWithValue("$a", isApp ? 1 : 0);
                    up.Parameters.AddWithValue("$id", id);
                    up.ExecuteNonQuery();
                }
                else
                {
                    r.Close();
                    using var ins = _con.CreateCommand();
                    ins.Transaction = _tx;
                    ins.CommandText = @"INSERT INTO files(path, name, ext, size, mtime, gen, pending, isapp) VALUES($p,$n,$e,$s,$m,$g,$pe,$a);
                                        INSERT INTO docs(rowid, name, body) VALUES(last_insert_rowid(), $n, '');";
                    ins.Parameters.AddWithValue("$p", f.FullName);
                    ins.Parameters.AddWithValue("$n", display);
                    ins.Parameters.AddWithValue("$e", ext);
                    ins.Parameters.AddWithValue("$s", size);
                    ins.Parameters.AddWithValue("$m", mtime);
                    ins.Parameters.AddWithValue("$g", _gen);
                    ins.Parameters.AddWithValue("$pe", wantsContent);
                    ins.Parameters.AddWithValue("$a", isApp ? 1 : 0);
                    ins.ExecuteNonQuery();
                }
            }

            public void Remove(string path)
            {
                _tx ??= _con.BeginTransaction();
                using var cmd = _con.CreateCommand();
                cmd.Transaction = _tx;
                // El archivo, o todo lo que estaba dentro si era una carpeta
                cmd.CommandText = @"DELETE FROM docs WHERE rowid IN (SELECT id FROM files WHERE path = $p OR path LIKE $dir ESCAPE '^');
                                    DELETE FROM files WHERE path = $p OR path LIKE $dir ESCAPE '^';";
                cmd.Parameters.AddWithValue("$p", path);
                string dir = path.TrimEnd('\\').Replace("^", "^^").Replace("%", "^%").Replace("_", "^_") + "\\%";
                cmd.Parameters.AddWithValue("$dir", dir);
                cmd.ExecuteNonQuery();
            }

            public void Commit()
            {
                _tx?.Commit();
                _tx?.Dispose();
                _tx = null;
            }
        }

        // ---------- Extracción de contenido ----------
        private bool ExtractBatch(SqliteConnection con, CancellationToken token)
        {
            var items = new List<(long id, string path, string ext, long pending)>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT id, path, ext, pending FROM files WHERE pending > 0 ORDER BY pending, mtime DESC LIMIT 50";
                using var r = cmd.ExecuteReader();
                while (r.Read()) items.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
            }
            if (items.Count == 0) return false;

            SetStatus(true, items[0].pending == 2
                ? $"Leyendo texto en imágenes (OCR)… faltan {PendingContent:N0}"
                : $"Leyendo contenido… faltan {PendingContent:N0}");

            bool ocr = AppSettings.Current.OcrEnabled && OcrService.Available;
            foreach (var (id, path, ext, pending) in items)
            {
                if (token.IsCancellationRequested || !_changes.IsEmpty || _ocrChanged) break;
                string body = "";
                try
                {
                    if (TextExtractor.IsImage(ext))
                    {
                        if (ocr) body = OcrService.RecognizeImageFile(path);
                    }
                    else if (ext == ".pdf")
                    {
                        body = TextExtractor.ExtractPdf(path, out var scanned);
                        // Páginas escaneadas (sin texto real): leerlas con OCR, máximo 30 por PDF
                        if (ocr && scanned.Count > 0)
                            body += " " + System.Text.RegularExpressions.Regex.Replace(
                                OcrService.RecognizePdfPages(path, scanned.Take(30)), @"\s+", " ");
                    }
                    else
                    {
                        body = TextExtractor.Extract(path, ext);
                    }
                }
                catch { /* protegido, dañado o en uso */ }

                using var tx = con.BeginTransaction();
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE docs SET body = $b WHERE rowid = $id; UPDATE files SET pending = 0 WHERE id = $id;";
                cmd.Parameters.AddWithValue("$b", body);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                tx.Commit();
                Thread.Sleep(2); // no acaparar el disco
            }
            return true;
        }

        private void UpdateCounts(SqliteConnection con)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(pending > 0),0) FROM files";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                FileCount = r.GetInt64(0);
                PendingContent = r.GetInt64(1);
            }
        }

        private void SetStatus(bool busy, string text)
        {
            if (IsBusy == busy && Status == text) return;
            IsBusy = busy;
            Status = text;
            StatusChanged?.Invoke();
        }

        // ---------- Cambios en vivo ----------
        private void StartWatchers()
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                try
                {
                    var w = new FileSystemWatcher(d.RootDirectory.FullName)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    w.Created += (_, e) => Enqueue(e.FullPath);
                    w.Changed += (_, e) => Enqueue(e.FullPath);
                    w.Deleted += (_, e) => Enqueue(e.FullPath);
                    w.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
                    w.Error += (_, _) => RequestRescan(); // se desbordó el búfer: re-rastrear
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch { /* unidad sin soporte de vigilancia */ }
            }
        }

        private void Enqueue(string path)
        {
            if (ShouldSkipPath(path)) return;
            _changes.Enqueue(path);
            _wake.Set();
        }

        private void ProcessChanges(SqliteConnection con)
        {
            if (_changes.IsEmpty) return;
            Thread.Sleep(300); // agrupar ráfagas de eventos
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_changes.TryDequeue(out var p)) paths.Add(p);

            var batch = new Batch(con, _gen);
            foreach (var p in paths)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        var f = new FileInfo(p);
                        if (f.Attributes.HasFlag(FileAttributes.Hidden) || f.Attributes.HasFlag(FileAttributes.System)) continue;
                        batch.Upsert(f, isApp: false);
                    }
                    else if (Directory.Exists(p))
                    {
                        // Carpeta nueva o renombrada: indexar su contenido
                        long seen = 0;
                        batch.Commit();
                        Crawl(p, false, batch, ref seen, _cts.Token);
                    }
                    else
                    {
                        batch.Remove(p);
                    }
                }
                catch { }
            }
            batch.Commit();
        }

        // ================= Búsqueda =================

        /// <summary>Convierte lo que escribe el usuario en una consulta FTS5 por prefijos.</summary>
        private static List<string> Terms(string query) =>
            query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(t => t.Trim('"', '*', '(', ')', ':', '^', '-', '+'))
                 .Where(t => t.Length > 0)
                 .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*")
                 .ToList();

        public static List<SearchResult> Search(string query, int limit = 40)
        {
            var results = new List<SearchResult>();
            var terms = Terms(query);
            if (terms.Count == 0 || !File.Exists(DbPath)) return results;

            using var con = Open(readOnly: true);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Coincidencias en el nombre (apps primero, luego lo más relevante y reciente)
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"SELECT f.path, f.name, f.isapp, f.mtime
                                    FROM docs JOIN files f ON f.id = docs.rowid
                                    WHERE docs MATCH $q
                                    ORDER BY f.isapp DESC, bm25(docs, 10.0, 0.0), f.mtime DESC
                                    LIMIT $l";
                cmd.Parameters.AddWithValue("$q", string.Join(" AND ", terms.Select(t => "name : " + t)));
                cmd.Parameters.AddWithValue("$l", 20);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string path = r.GetString(0);
                    if (!seen.Add(path)) continue;
                    results.Add(new SearchResult
                    {
                        Path = path,
                        Name = r.GetString(1),
                        Kind = r.GetInt64(2) == 1 ? ResultKind.App : ResultKind.File,
                        Modified = new DateTime(r.GetInt64(3), DateTimeKind.Utc).ToLocalTime()
                    });
                }
            }

            // 2) Coincidencias dentro del contenido
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"SELECT f.path, f.name, f.mtime, snippet(docs, 1, char(1), char(2), '…', 14)
                                    FROM docs JOIN files f ON f.id = docs.rowid
                                    WHERE docs MATCH $q
                                    ORDER BY bm25(docs, 0.0, 1.0)
                                    LIMIT $l";
                cmd.Parameters.AddWithValue("$q", string.Join(" AND ", terms.Select(t => "body : " + t)));
                cmd.Parameters.AddWithValue("$l", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string path = r.GetString(0);
                    if (!seen.Add(path)) continue;
                    results.Add(new SearchResult
                    {
                        Path = path,
                        Name = r.GetString(1),
                        Kind = ResultKind.Content,
                        Modified = new DateTime(r.GetInt64(2), DateTimeKind.Utc).ToLocalTime(),
                        Snippet = r.IsDBNull(3) ? "" : r.GetString(3)
                    });
                }
            }

            // Quitar resultados de archivos que ya no existen
            return results.Where(x => File.Exists(x.Path)).Take(limit).ToList();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _wake.Set();
            foreach (var w in _watchers) w.Dispose();
        }
    }
}
