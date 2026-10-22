using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private IslandWindow? _island;
        private NotificationService? _service;
        private Forms.NotifyIcon? _tray;
        private Forms.ToolStripMenuItem? _modeItem;

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

            _island = new IslandWindow();
            _island.Show();

            _service = new NotificationService();
            _service.NotificationArrived += n => _island.Push(n);
            _island.Dismissed += n => _service.Remove(n);
            _island.ClearedAll += () => _service.ClearAll();

            CreateTray();

            var media = new MediaService(Dispatcher);
            _island.AttachMedia(media);
            await media.StartAsync();

            var mode = await _service.StartAsync();
            UpdateModeLabel(mode);

            if (mode == ListenerMode.None)
            {
                _island.Push(new IslandNotification
                {
                    AppName = "Dynamic Island",
                    Title = "Necesito acceso a tus notificaciones",
                    Body = "Haz clic aquí y activa \"Acceso a notificaciones\" para esta app.",
                    OnClick = OpenNotificationPrivacySettings
                });
            }
            else
            {
                _island.Push(new IslandNotification
                {
                    AppName = "Dynamic Island",
                    Title = "Lista ✨",
                    Body = mode == ListenerMode.WindowsApi
                        ? "Escuchando notificaciones de Windows."
                        : "Escuchando notificaciones (modo compatibilidad)."
                });
            }
        }

        private void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();

            _modeItem = new Forms.ToolStripMenuItem("Iniciando…") { Enabled = false };
            menu.Items.Add(_modeItem);
            menu.Items.Add(new Forms.ToolStripSeparator());

            var pause = new Forms.ToolStripMenuItem("Pausar banners (No molestar)") { CheckOnClick = true };
            pause.CheckedChanged += (_, _) => _island!.Paused = pause.Checked;
            menu.Items.Add(pause);

            var duration = new Forms.ToolStripMenuItem("Duración del banner");
            foreach (int s in new[] { 3, 5, 8, 12 })
            {
                var item = new Forms.ToolStripMenuItem($"{s} segundos") { Checked = s == 5, Tag = s };
                item.Click += (_, _) =>
                {
                    _island!.BannerSeconds = s;
                    foreach (Forms.ToolStripMenuItem i in duration.DropDownItems) i.Checked = (int)i.Tag! == s;
                };
                duration.DropDownItems.Add(item);
            }
            menu.Items.Add(duration);

            var trackChanges = new Forms.ToolStripMenuItem("Avisar al cambiar de canción") { Checked = true, CheckOnClick = true };
            trackChanges.CheckedChanged += (_, _) => _island!.ShowTrackChanges = trackChanges.Checked;
            menu.Items.Add(trackChanges);

            var startup = new Forms.ToolStripMenuItem("Iniciar con Windows") { Checked = StartupManager.IsEnabled, CheckOnClick = true };
            startup.CheckedChanged += (_, _) => StartupManager.SetEnabled(startup.Checked);
            menu.Items.Add(startup);

            var test = new Forms.ToolStripMenuItem("Probar la isla");
            test.Click += (_, _) => _island!.Push(new IslandNotification
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
