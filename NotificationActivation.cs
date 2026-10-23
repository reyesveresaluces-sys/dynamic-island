using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace DynamicIsland
{
    /// <summary>
    /// Abre una notificación exactamente como lo hace el Centro de notificaciones de Windows:
    /// 1) Toma la "orden de apertura" original (atributo launch del toast) de la base de notificaciones.
    /// 2) Se la entrega al activador COM de la app (INotificationActivationCallback), igual que Windows.
    ///    Para Edge/Chrome esto abre la pestaña y el chat exactos (p. ej. WhatsApp Web).
    /// 3) Respaldo para navegadores Chromium: --notification-launch-id por línea de comandos.
    /// 4) Respaldo final: buscar la pestaña del sitio, o abrir la app.
    /// </summary>
    public static class NotificationActivation
    {
        private static readonly string WpnDb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");

        private static readonly string LogPath = Path.Combine(SearchIndex.DataDir, "clicks.log");

        public static void Open(IslandNotification n)
        {
            // Hay que hacerlo en el hilo del clic: permite que la app abierta pase al frente
            AllowSetForegroundWindow(ASFW_ANY);

            uint id = n.Id;
            string appId = n.AppId, appName = n.AppName, title = n.Title, body = n.Body;
            string launch = n.LaunchArgs, site = n.Site;

            Task.Run(() =>
            {
                try
                {
                    if (launch.Length == 0 || site.Length == 0)
                    {
                        var original = FindOriginal(id, appId, title, body);
                        if (original != null)
                        {
                            if (launch.Length == 0) launch = original.Value.launch;
                            if (site.Length == 0) site = original.Value.site;
                        }
                    }
                    Log($"Clic: app={appId} sitio={site} launch={(launch.Length > 0 ? launch : "(no encontrado)")}");

                    bool browser = BrowserTabs.IsBrowser(appId, appName);
                    if (launch.Length > 0)
                    {
                        // Chromium (Edge/Chrome): Windows termina ejecutando el navegador con
                        // --notification-launch-id; hacerlo directamente es lo más fiable.
                        if (browser && TryChromiumSwitch(appId, appName, launch)) return;
                        if (TryComActivator(appId, launch)) return;
                        if (!browser && TryChromiumSwitch(appId, appName, launch)) return;
                    }

                    if (browser)
                    {
                        if (site.Length > 0)
                        {
                            Log("Respaldo: buscar la pestaña del sitio");
                            BrowserTabs.FocusOrOpen(appId, appName, site);
                        }
                        else
                        {
                            // Nunca abrir una pestaña nueva vacía: solo traer el navegador al frente
                            Log("Respaldo: traer el navegador al frente");
                            BrowserTabs.FocusBrowser(appId, appName);
                        }
                        return;
                    }

                    Log("Respaldo: abrir la app");
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log("Error: " + ex.Message);
                }
            });
        }

        // ================= 1) Notificación original en la base de Windows =================

        private static string Fingerprint(string title, string body) =>
            Regex.Replace(title + " " + body, @"\s+", " ").Trim().ToLowerInvariant();

        /// <summary>"Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!App" → "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe".</summary>
        private static string Family(string aumid)
        {
            int i = aumid.IndexOf('!');
            return i > 0 ? aumid[..i] : aumid;
        }

        /// <summary>Edge registra cada sitio como "…!https://web.whatsapp.com/": de ahí sale el dominio.</summary>
        private static string SiteFromHandler(string handler)
        {
            int i = handler.IndexOf('!');
            if (i < 0) return "";
            return BrowserTabs.AsDomain(handler[(i + 1)..]) ?? "";
        }

        private static List<(long id, string handler, string payload)> ReadToasts()
        {
            var rows = new List<(long, string, string)>();
            void Read(string db)
            {
                using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = @"SELECT n.Id, IFNULL(h.PrimaryId,''), n.Payload
                                    FROM Notification n
                                    LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                                    WHERE n.Type = 'toast'
                                    ORDER BY n.Id DESC LIMIT 400";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(2)) continue;
                    string payload = r.GetValue(2) is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : r.GetString(2);
                    rows.Add((r.GetInt64(0), r.GetString(1), payload));
                }
            }

            try { Read(WpnDb); }
            catch (Exception ex)
            {
                // Windows puede tener la base bloqueada: leer una copia temporal (con su WAL)
                Log("Base de notificaciones ocupada (" + ex.Message + "), leyendo una copia");
                rows.Clear();
                string tmp = Path.Combine(Path.GetTempPath(), "DynamicIsland-wpn");
                Directory.CreateDirectory(tmp);
                string copy = Path.Combine(tmp, "wpn.db");
                try
                {
                    foreach (var ext in new[] { "", "-wal", "-shm" })
                    {
                        string src = WpnDb + ext, dst = copy + ext;
                        if (File.Exists(dst)) File.Delete(dst);
                        if (!File.Exists(src)) continue;
                        using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var o = File.Create(dst);
                        i.CopyTo(o);
                    }
                    Read(copy);
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    foreach (var ext in new[] { "", "-wal", "-shm" })
                        try { File.Delete(copy + ext); } catch { }
                }
            }
            return rows;
        }

        /// <summary>Busca el XML original del toast para obtener su atributo launch y el sitio.</summary>
        public static (string launch, string site)? FindOriginal(uint id, string appId, string title, string body)
        {
            if (!File.Exists(WpnDb)) { Log("No existe la base de notificaciones"); return null; }
            string want = Fingerprint(title, body);
            string wantTitle = Fingerprint(title, "");
            string family = Family(appId);

            List<(long id, string handler, string payload)> rows;
            try { rows = ReadToasts(); }
            catch (Exception ex) { Log("No pude leer la base de notificaciones: " + ex.Message); return null; }

            (string launch, string site)? Result(string handler, (string title, string body, string site, string launch) t)
            {
                string site = t.site.Length > 0 ? t.site : SiteFromHandler(handler);
                return (t.launch, site);
            }

            // 1) Mismo Id que la notificación de Windows (exacto)
            if (id != 0)
            {
                foreach (var row in rows.Where(r => r.id == id))
                {
                    var t = NotificationService.ParseToastXml(row.payload);
                    Log($"Coincidencia por Id {id} (origen {row.handler})");
                    return Result(row.handler, t);
                }
            }

            // 2) Mismo texto. Preferir la misma app (o la misma familia: Edge usa "…!App" en la API
            //    pero "…!https://sitio/" en la base); si no, cualquier app con el mismo texto.
            (string launch, string site)? sameText = null, sameTitle = null;
            int score = -1;
            foreach (var (_, handler, payload) in rows)
            {
                var t = NotificationService.ParseToastXml(payload);
                if (t.launch.Length == 0) continue;

                int s = handler.Equals(appId, StringComparison.OrdinalIgnoreCase) ? 2
                      : family.Length > 0 && Family(handler).Equals(family, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                if (Fingerprint(t.title, t.body) == want)
                {
                    if (s > score) { score = s; sameText = Result(handler, t); }
                    if (s == 2) break;
                }
                else if (sameTitle == null && s > 0 && wantTitle.Length > 0 && Fingerprint(t.title, "") == wantTitle)
                {
                    sameTitle = Result(handler, t);
                }
            }

            if (sameText != null) { Log($"Coincidencia por texto (nivel {score})"); return sameText; }
            if (sameTitle != null) { Log("Coincidencia por título"); return sameTitle; }

            var handlers = rows.Select(r => r.handler).Distinct().Take(15);
            Log($"Sin coincidencia entre {rows.Count} notificaciones. Orígenes: {string.Join(", ", handlers)}");
            return null;
        }

        // ================= 2) Activador COM (lo mismo que hace Windows) =================

        [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INotificationActivationCallback
        {
            [PreserveSig]
            int Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                         [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
                         IntPtr data, uint count);
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        private const uint CLSCTX_LOCAL_SERVER = 0x4;

        private static bool TryComActivator(string appId, string launch)
        {
            var clsid = FindActivatorClsid(appId);
            if (clsid == null)
            {
                Log("Sin activador COM registrado para " + appId);
                return false;
            }
            try
            {
                Guid c = clsid.Value, iid = typeof(INotificationActivationCallback).GUID;
                int hr = CoCreateInstance(ref c, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out object obj);
                if (hr != 0) { Log($"CoCreateInstance {c} falló: 0x{hr:X8}"); return false; }
                try
                {
                    hr = ((INotificationActivationCallback)obj).Activate(appId, launch, IntPtr.Zero, 0);
                    Log($"Activador {c}: 0x{hr:X8}");
                    return hr >= 0;
                }
                finally { Marshal.ReleaseComObject(obj); }
            }
            catch (Exception ex)
            {
                Log("Activador COM: " + ex.Message);
                return false;
            }
        }

        private static readonly object CacheGate = new();
        private static Dictionary<string, Guid>? _activators;

        private static Guid? FindActivatorClsid(string appId)
        {
            foreach (var id in new[] { appId, BaseAumid(appId) }.Distinct())
            {
                if (string.IsNullOrEmpty(id)) continue;

                // a) Registro del AUMID (apps que se registran con CustomActivator)
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using var key = root.OpenSubKey($@"Software\Classes\AppUserModelId\{id}");
                    if (key?.GetValue("CustomActivator") is string s && Guid.TryParse(s, out var g)) return g;
                }

                // b) Acceso directo del menú Inicio con ToastActivatorCLSID (así lo hacen Edge y Chrome)
                lock (CacheGate) _activators ??= ScanShortcuts();
                if (_activators.TryGetValue(id, out var fromLnk)) return fromLnk;
            }
            return null;
        }

        /// <summary>"MSEdge._crx_abc…" (sitio instalado como app) → "MSEdge".</summary>
        private static string BaseAumid(string aumid)
        {
            int i = aumid.IndexOf("._crx_", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? aumid[..i] : aumid;
        }

        // ---------- Propiedades de accesos directos ----------

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr ptr;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

        private static readonly Guid AppUserModelFmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const ushort VT_LPWSTR = 31, VT_CLSID = 72;

        private static Dictionary<string, Guid> ScanShortcuts()
        {
            var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            Guid iid = typeof(IPropertyStore).GUID;

            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", opts))
                {
                    try
                    {
                        if (SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 0, ref iid, out var store) != 0 || store == null) continue;
                        try
                        {
                            string? id = ReadString(store, 5);
                            Guid? clsid = ReadGuid(store, 26);
                            if (!string.IsNullOrEmpty(id) && clsid != null && !map.ContainsKey(id)) map[id] = clsid.Value;
                        }
                        finally { Marshal.ReleaseComObject(store); }
                    }
                    catch { }
                }
            }
            Log($"Accesos directos con activador: {string.Join(", ", map.Keys)}");
            return map;
        }

        private static string? ReadString(IPropertyStore store, uint pid)
        {
            var key = new PROPERTYKEY { fmtid = AppUserModelFmtid, pid = pid };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == VT_LPWSTR && pv.ptr != IntPtr.Zero ? Marshal.PtrToStringUni(pv.ptr) : null; }
            finally { PropVariantClear(ref pv); }
        }

        private static Guid? ReadGuid(IPropertyStore store, uint pid)
        {
            var key = new PROPERTYKEY { fmtid = AppUserModelFmtid, pid = pid };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == VT_CLSID && pv.ptr != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(pv.ptr) : null; }
            finally { PropVariantClear(ref pv); }
        }

        // ================= 3) Respaldo Chromium: --notification-launch-id =================

        private static bool TryChromiumSwitch(string appId, string appName, string launch)
        {
            string? process = BrowserTabs.BrowserProcess(appId, appName);
            if (process == null || process == "firefox") return false;

            string? exe = null;
            try
            {
                exe = Process.GetProcessesByName(process).Select(p =>
                {
                    try { return p.MainModule?.FileName; } catch { return null; }
                }).FirstOrDefault(f => !string.IsNullOrEmpty(f));
            }
            catch { }
            exe ??= Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{process}.exe")?.GetValue("") as string;
            exe ??= Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{process}.exe")?.GetValue("") as string;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { Log("No encontré el ejecutable de " + process); return false; }

            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                const string Prefix = "--notification-launch-id=";
                // Edge/Chrome ya guardan el launch con el prefijo incluido
                psi.ArgumentList.Add(launch.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? launch : Prefix + launch);
                Process.Start(psi);
                Log("Abierto con --notification-launch-id en " + exe);
                return true;
            }
            catch (Exception ex)
            {
                Log("--notification-launch-id: " + ex.Message);
                return false;
            }
        }

        // ================= Utilidades =================

        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
        private const int ASFW_ANY = -1;

        private static readonly object LogGate = new();

        public static void Log(string message)
        {
            try
            {
                lock (LogGate)
                {
                    Directory.CreateDirectory(SearchIndex.DataDir);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Delete(LogPath);
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
