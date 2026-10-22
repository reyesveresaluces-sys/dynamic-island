using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private readonly List<IslandWindow> _islands = new();
        private NotificationService? _service;
        private MediaService? _media;
        private Forms.NotifyIcon? _tray;
        private Forms.ToolStripMenuItem? _modeItem;
        private Forms.ToolStripMenuItem? _searchItem;
        private Forms.ToolStripMenuItem? _indexItem;
        private SearchWindow? _search;
        private SearchIndex? _index;

        // Ajustes compartidos por todas las islas
        private bool _allScreens = true;
        private bool _paused;
        private double _bannerSeconds = 5;
        private bool _showTrackChanges = true;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Una sola instancia
            _mutex = new Mutex(true, "DynamicIsland.SingleInstance.6f1c", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            _media = new MediaService(Dispatcher);
            SyncScreens();

            // Monitores conectados/desconectados o cambio de resolución
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(SyncScreens), DispatcherPriority.Background);

            StartKeepOnTop();

            _service = new NotificationService();
            _service.NotificationArrived += PushAll;

            CreateTray();
            StartSearch();

            await _media.StartAsync();

            var mode = await _service.StartAsync();
            UpdateModeLabel(mode);

            if (mode == ListenerMode.None)
            {
                PushAll(new IslandNotification
                {
                    AppName = "Dynamic Island",
                    Title = "Necesito acceso a tus notificaciones",
                    Body = "Haz clic aquí y activa \"Acceso a notificaciones\" para esta app.",
                    OnClick = OpenNotificationPrivacySettings
                });
            }
            else
            {
                PushAll(new IslandNotification
                {
                    AppName = "Dynamic Island",
                    Title = "Lista ✨",
                    Body = mode == ListenerMode.WindowsApi
                        ? "Escuchando notificaciones de Windows."
                        : "Escuchando notificaciones (modo compatibilidad)."
                });
            }
        }

        // ---------------- Una isla por monitor ----------------

        /// <summary>Crea, cierra o reubica islas según los monitores conectados.</summary>
        private void SyncScreens()
        {
            var screens = _allScreens
                ? Forms.Screen.AllScreens
                : new[] { Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0] };
            var ids = screens.Select(s => s.DeviceName).ToHashSet();

            // Cerrar islas de monitores que ya no están
            foreach (var w in _islands.Where(w => !ids.Contains(w.ScreenId)).ToList())
            {
                _islands.Remove(w);
                w.Close();
            }

            // Crear islas para monitores nuevos
            foreach (var id in ids)
            {
                if (_islands.Any(w => w.ScreenId == id)) continue;
                var island = CreateIsland(id);
                _islands.Add(island);
                island.Show();
            }

            foreach (var w in _islands) w.PositionWindow();
        }

        private IslandWindow CreateIsland(string screenId)
        {
            var island = new IslandWindow(screenId)
            {
                Paused = _paused,
                BannerSeconds = _bannerSeconds,
                ShowTrackChanges = _showTrackChanges
            };

            // Copiar el historial de una isla existente
            var existing = _islands.FirstOrDefault();
            if (existing != null) island.LoadHistory(existing.History.ToList());

            island.Dismissed += n =>
            {
                _service?.Remove(n);
                foreach (var w in _islands) if (w != island) w.RemoveItem(n);
            };
            island.ClearedAll += () =>
            {
                _service?.ClearAll();
                foreach (var w in _islands) if (w != island) w.ClearHistory();
            };
            island.HistoryOpened += () =>
            {
                foreach (var w in _islands) if (w != island) w.MarkSeen();
            };

            if (_media != null) island.AttachMedia(_media);
            return island;
        }

        // ---------------- Buscador (Alt+Espacio) ----------------

        private void StartSearch()
        {
            _search = new SearchWindow();
            _search.Initialize(); // registra el atajo global

            if (_searchItem != null)
                _searchItem.Text = _search.HotkeyRegistered ? $"Buscar…  ({_search.HotkeyLabel})" : "Buscar…";

            if (!_search.HotkeyRegistered)
            {
                PushAll(new IslandNotification
                {
                    AppName = "Buscador",
                    Title = "No pude registrar el atajo",
                    Body = "Alt+Espacio está ocupado por otra app. Abre el buscador desde el ícono de la bandeja."
                });
            }
            else if (_search.HotkeyLabel != "Alt+Espacio")
            {
                PushAll(new IslandNotification
                {
                    AppName = "Buscador",
                    Title = $"Usa {_search.HotkeyLabel}",
                    Body = "Alt+Espacio está ocupado por otra app (por ejemplo PowerToys Run)."
                });
            }

            _index = new SearchIndex();
            _index.StatusChanged += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_index == null) return;
                _search?.SetIndexStatus(_index.Status, _index.IsBusy);
                if (_indexItem != null) _indexItem.Text = "Índice: " + _index.Status;
            }));
            _index.Start();

            // El OCR necesita un idioma de Windows con reconocimiento de texto instalado
            if (AppSettings.Current.OcrEnabled)
            {
                System.Threading.Tasks.Task.Run(() => OcrService.Available).ContinueWith(t =>
                {
                    if (t.Result) return;
                    PushAll(new IslandNotification
                    {
                        AppName = "Buscador",
                        Title = "OCR no disponible",
                        Body = "Agrega el idioma Español en Configuración → Hora e idioma para leer texto en imágenes.",
                        OnClick = () =>
                        {
                            try { Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); } catch { }
                        }
                    });
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        // ---------------- Mantener la isla siempre visible ----------------

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private WinEventDelegate? _winEventProc; // referencia viva para que el GC no lo recolecte
        private IntPtr _foregroundHook, _restoreHook;
        private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>
        /// Cuando otra ventana o pestaña pasa al frente, Windows puede dejar la isla detrás.
        /// Escuchamos esos cambios y la volvemos a subir, más un respaldo cada segundo.
        /// </summary>
        private void StartKeepOnTop()
        {
            _winEventProc = (_, _, _, _, _, _, _) => KeepOnTop();
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            _restoreHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

            _topTimer.Tick += (_, _) => KeepOnTop();
            _topTimer.Start();
        }

        private void KeepOnTop()
        {
            foreach (var w in _islands) w.BringToTop();
        }

        private void PushAll(IslandNotification n)
        {
            foreach (var w in _islands) w.Push(n);
        }

        private void ForEachIsland(Action<IslandWindow> action)
        {
            foreach (var w in _islands) action(w);
        }

        // ---------------- Bandeja del sistema ----------------

        private void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();

            _modeItem = new Forms.ToolStripMenuItem("Iniciando…") { Enabled = false };
            menu.Items.Add(_modeItem);
            menu.Items.Add(new Forms.ToolStripSeparator());

            _searchItem = new Forms.ToolStripMenuItem("Buscar…");
            _searchItem.Click += (_, _) => _search?.ShowBar();
            menu.Items.Add(_searchItem);

            _indexItem = new Forms.ToolStripMenuItem("Índice: preparando…") { Enabled = false };
            menu.Items.Add(_indexItem);

            var reindex = new Forms.ToolStripMenuItem("Volver a indexar ahora");
            reindex.Click += (_, _) => _index?.RequestRescan();
            menu.Items.Add(reindex);

            var ocrItem = new Forms.ToolStripMenuItem("Leer texto en imágenes y PDF escaneados (OCR)")
            {
                Checked = AppSettings.Current.OcrEnabled,
                CheckOnClick = true
            };
            ocrItem.CheckedChanged += (_, _) =>
            {
                AppSettings.Current.OcrEnabled = ocrItem.Checked;
                AppSettings.Current.Save();
                _index?.OcrSettingChanged();
            };
            menu.Items.Add(ocrItem);
            menu.Items.Add(new Forms.ToolStripSeparator());

            var allScreens = new Forms.ToolStripMenuItem("Mostrar en todas las pantallas") { Checked = _allScreens, CheckOnClick = true };
            allScreens.CheckedChanged += (_, _) =>
            {
                _allScreens = allScreens.Checked;
                SyncScreens();
            };
            menu.Items.Add(allScreens);

            var pause = new Forms.ToolStripMenuItem("Pausar banners (No molestar)") { CheckOnClick = true };
            pause.CheckedChanged += (_, _) =>
            {
                _paused = pause.Checked;
                ForEachIsland(w => w.Paused = _paused);
            };
            menu.Items.Add(pause);

            var duration = new Forms.ToolStripMenuItem("Duración del banner");
            foreach (int s in new[] { 3, 5, 8, 12 })
            {
                var item = new Forms.ToolStripMenuItem($"{s} segundos") { Checked = s == 5, Tag = s };
                item.Click += (_, _) =>
                {
                    _bannerSeconds = s;
                    ForEachIsland(w => w.BannerSeconds = s);
                    foreach (Forms.ToolStripMenuItem i in duration.DropDownItems) i.Checked = (int)i.Tag! == s;
                };
                duration.DropDownItems.Add(item);
            }
            menu.Items.Add(duration);

            var trackChanges = new Forms.ToolStripMenuItem("Avisar al cambiar de canción") { Checked = true, CheckOnClick = true };
            trackChanges.CheckedChanged += (_, _) =>
            {
                _showTrackChanges = trackChanges.Checked;
                ForEachIsland(w => w.ShowTrackChanges = _showTrackChanges);
            };
            menu.Items.Add(trackChanges);

            var startup = new Forms.ToolStripMenuItem("Iniciar con Windows") { Checked = StartupManager.IsEnabled, CheckOnClick = true };
            startup.CheckedChanged += (_, _) => StartupManager.SetEnabled(startup.Checked);
            menu.Items.Add(startup);

            var test = new Forms.ToolStripMenuItem("Probar la isla");
            test.Click += (_, _) => PushAll(new IslandNotification
            {
                AppName = "Prueba",
                Title = "Hola 👋",
                Body = "Así se verán tus notificaciones en la isla."
            });
            menu.Items.Add(test);

            var settings = new Forms.ToolStripMenuItem("Permisos de notificaciones de Windows…");
            settings.Click += (_, _) => OpenNotificationPrivacySettings();
            menu.Items.Add(settings);

            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = new Forms.ToolStripMenuItem("Salir");
            exit.Click += (_, _) => ExitApp();
            menu.Items.Add(exit);

            _tray = new Forms.NotifyIcon
            {
                Icon = TrayIcon.Create(),
                Text = "Dynamic Island",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += (_, a) =>
            {
                if (a.Button == Forms.MouseButtons.Left) test.PerformClick();
            };
        }

        private void UpdateModeLabel(ListenerMode mode)
        {
            if (_modeItem == null) return;
            _modeItem.Text = mode switch
            {
                ListenerMode.WindowsApi => "● Conectado a notificaciones de Windows",
                ListenerMode.Database => "● Conectado (modo compatibilidad)",
                _ => "○ Sin acceso a notificaciones"
            };
        }

        private static void OpenNotificationPrivacySettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
            }
            catch { }
        }

        private void ExitApp()
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _topTimer.Stop();
            _index?.Dispose();
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            if (_restoreHook != IntPtr.Zero) UnhookWinEvent(_restoreHook);
            _tray?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
