using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland
{
    public enum ListenerMode { None, WindowsApi, Database }

    /// <summary>
    /// Lee las notificaciones del sistema.
    /// Modo 1: API oficial UserNotificationListener (íconos y nombres reales).
    /// Modo 2 (respaldo): lectura de la base de notificaciones de Windows (wpndatabase.db).
    /// Ambos funcionan por sondeo (cada segundo), porque el evento NotificationChanged
    /// no está disponible para apps de escritorio sin empaquetar.
    /// </summary>
    public sealed class NotificationService
    {
        public event Action<IslandNotification>? NotificationArrived;
        public ListenerMode Mode { get; private set; } = ListenerMode.None;
        public string? LastError { get; private set; }

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
        private UserNotificationListener? _listener;
        private HashSet<uint> _known = new();
        private long _lastDbId;
        private bool _busy;

        private static readonly string DbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");

        public async Task<ListenerMode> StartAsync()
        {
            // 1) API de Windows
            try
            {
                _listener = UserNotificationListener.Current;
                var access = await _listener.RequestAccessAsync();
                if (access == UserNotificationListenerAccessStatus.Allowed)
                {
                    var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    _known = current.Select(n => n.Id).ToHashSet();
                    Mode = ListenerMode.WindowsApi;
                }
                else
                {
                    LastError = "Acceso a notificaciones denegado en Configuración de Windows.";
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _listener = null;
            }

            // 2) Respaldo: base de datos de notificaciones
            if (Mode == ListenerMode.None && File.Exists(DbPath))
            {
                try
                {
                    _lastDbId = await Task.Run(() =>
                    {
                        using var con = OpenDb();
                        using var cmd = con.CreateCommand();
                        cmd.CommandText = "SELECT IFNULL(MAX(Id),0) FROM Notification";
                        return Convert.ToInt64(cmd.ExecuteScalar());
                    });
                    Mode = ListenerMode.Database;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }

            if (Mode != ListenerMode.None)
            {
                _timer.Tick += async (_, _) => await PollAsync();
                _timer.Start();
            }
            return Mode;
        }

        private async Task PollAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                if (Mode == ListenerMode.WindowsApi) await PollApiAsync();
                else if (Mode == ListenerMode.Database) await PollDbAsync();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        // ---------- Modo API ----------
        private async Task PollApiAsync()
        {
            var list = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = new HashSet<uint>();
            foreach (var n in list.OrderBy(x => x.CreationTime))
            {
                ids.Add(n.Id);
                if (_known.Contains(n.Id)) continue;
                var item = await ParseApiAsync(n);
                if (item != null) NotificationArrived?.Invoke(item);
            }
            _known = ids;
        }

        private static async Task<IslandNotification?> ParseApiAsync(UserNotification n)
        {
            string title = "", body = "";
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements()?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                            ?? new List<string>();
                title = texts.FirstOrDefault() ?? "";
                body = string.Join("\n", texts.Skip(1));
            }
            catch { /* plantilla no estándar */ }

            string appName = "Aplicación", appId = "";
            BitmapImage? icon = null;
            try
            {
                appName = n.AppInfo?.DisplayInfo?.DisplayName ?? appName;
                appId = n.AppInfo?.AppUserModelId ?? "";
                var logoRef = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
                if (logoRef != null)
                {
                    using var ras = await logoRef.OpenReadAsync();
                    using var src = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await src.CopyToAsync(ms);
                    ms.Position = 0;
                    icon = new BitmapImage();
                    icon.BeginInit();
                    icon.CacheOption = BitmapCacheOption.OnLoad;
                    icon.StreamSource = ms;
                    icon.EndInit();
                    icon.Freeze();
                }
            }
            catch { icon = null; }

            // Notificaciones web: el navegador agrega el dominio del sitio (p. ej. web.whatsapp.com)
            string site = "";
            if (BrowserTabs.IsBrowser(appId, appName))
            {
                var lines = (title + "\n" + body).Split('\n').Where(t => t.Trim().Length > 0).ToList();
                site = lines.Select(BrowserTabs.AsDomain).FirstOrDefault(d => d != null) ?? "";
                if (site.Length > 0)
                {
                    lines = lines.Where(t => BrowserTabs.AsDomain(t) == null).ToList();
                    title = lines.FirstOrDefault() ?? "";
                    body = string.Join("\n", lines.Skip(1));
                    appName = $"{appName} · {site}";
                }
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return null;

            return new IslandNotification
            {
                Id = n.Id,
                AppName = appName,
                AppId = appId,
                Title = title,
                Body = body,
                Time = n.CreationTime.LocalDateTime,
                Icon = icon,
                Site = site
            };
        }

        public void Remove(IslandNotification n)
        {
            try
            {
                if (Mode == ListenerMode.WindowsApi && n.Id != 0) _listener?.RemoveNotification(n.Id);
            }
            catch { }
        }

        public void ClearAll()
        {
            try
            {
                if (Mode == ListenerMode.WindowsApi) _listener?.ClearNotifications();
            }
            catch { }
        }

        // ---------- Modo base de datos (respaldo) ----------
        private static SqliteConnection OpenDb()
        {
            var con = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Cache=Shared");
            con.Open();
            return con;
        }

        private async Task PollDbAsync()
        {
            long last = _lastDbId;
            var rows = await Task.Run(() =>
            {
                var result = new List<(long id, string payload, long arrival, string appId)>();
                using var con = OpenDb();
                using var cmd = con.CreateCommand();
                cmd.CommandText = @"SELECT n.Id, n.Payload, n.ArrivalTime, IFNULL(h.PrimaryId,'')
                                    FROM Notification n
                                    LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                                    WHERE n.Type = 'toast' AND n.Id > $last
                                    ORDER BY n.Id";
                cmd.Parameters.AddWithValue("$last", last);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string payload = r.IsDBNull(1) ? "" :
                        r.GetValue(1) is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : r.GetString(1);
                    long arrival = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    result.Add((r.GetInt64(0), payload, arrival, r.GetString(3)));
                }
                return result;
            });

            foreach (var row in rows)
            {
                _lastDbId = Math.Max(_lastDbId, row.id);
                var (title, body, site) = ParseToastXml(row.payload);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) continue;
                string appName = PrettyAppName(row.appId);
                if (site.Length > 0) appName = $"{appName} · {site}";
                DateTime time;
                try { time = row.arrival > 0 ? DateTime.FromFileTime(row.arrival) : DateTime.Now; }
                catch { time = DateTime.Now; }

                NotificationArrived?.Invoke(new IslandNotification
                {
                    Id = 0,
                    AppName = appName,
                    AppId = row.appId,
                    Title = title,
                    Body = body,
                    Time = time,
                    Site = site
                });
            }
        }

        private static (string title, string body, string site) ParseToastXml(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml.TrimEnd('\0'));
                var binding = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "binding");
                string site = "";
                var texts = new List<string>();
                foreach (var e in (binding ?? doc.Root!).Descendants().Where(x => x.Name.LocalName == "text"))
                {
                    string v = e.Value.Trim();
                    if (v.Length == 0) continue;
                    bool attribution = (string?)e.Attribute("placement") == "attribution";
                    string? domain = BrowserTabs.AsDomain(v);
                    if (domain != null && (attribution || texts.Count > 0))
                    {
                        site = domain; // el sitio web que mandó la notificación
                        continue;
                    }
                    if (attribution) continue;
                    texts.Add(v);
                }
                return (texts.FirstOrDefault() ?? "", string.Join("\n", texts.Skip(1)), site);
            }
            catch { return ("", "", ""); }
        }

        /// <summary>Convierte un AUMID en un nombre legible: "Microsoft.Windows.Explorer" → "Explorer".</summary>
        internal static string PrettyAppName(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return "Aplicación";
            string s = aumid;
            if (s.Contains('\\')) s = Path.GetFileNameWithoutExtension(s);
            int bang = s.IndexOf('!');
            if (bang > 0) s = s[..bang];
            int us = s.IndexOf('_');
            if (us > 0) s = s[..us];
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            s = parts.LastOrDefault(p => !p.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? s;
            if (s.StartsWith("MS") && s.Length > 2 && char.IsUpper(s[2])) s = s[2..]; // MSTeams → Teams
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : "Aplicación";
        }
    }
}
