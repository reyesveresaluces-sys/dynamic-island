using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

            _service = new NotificationService();
            _service.NotificationArrived += PushAll;

            CreateTray();

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
            _tray?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
