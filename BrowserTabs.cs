using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace DynamicIsland
{
    /// <summary>
    /// Abre la pestaña correcta para notificaciones web (WhatsApp Web, Gmail, Teams…).
    /// Windows no permite que otra app active la notificación original, así que buscamos
    /// la pestaña del sitio en el navegador con UI Automation y la seleccionamos.
    /// </summary>
    public static class BrowserTabs
    {
        private static readonly (string key, string process)[] Browsers =
        {
            ("msedge", "msedge"), ("edge", "msedge"),
            ("chrome", "chrome"), ("brave", "brave"),
            ("opera", "opera"), ("vivaldi", "vivaldi"), ("firefox", "firefox")
        };

        /// <summary>Nombre que muestra la pestaña de sitios conocidos.</summary>
        private static readonly Dictionary<string, string[]> KnownSites = new(StringComparer.OrdinalIgnoreCase)
        {
            ["web.whatsapp.com"] = new[] { "WhatsApp" },
            ["mail.google.com"] = new[] { "Gmail" },
            ["calendar.google.com"] = new[] { "Calendar", "Calendario" },
            ["chat.google.com"] = new[] { "Chat" },
            ["meet.google.com"] = new[] { "Meet" },
            ["outlook.live.com"] = new[] { "Outlook" },
            ["outlook.office.com"] = new[] { "Outlook" },
            ["outlook.office365.com"] = new[] { "Outlook" },
            ["teams.microsoft.com"] = new[] { "Teams" },
            ["teams.live.com"] = new[] { "Teams" },
            ["web.telegram.org"] = new[] { "Telegram" },
            ["discord.com"] = new[] { "Discord" },
            ["app.slack.com"] = new[] { "Slack" },
            ["www.messenger.com"] = new[] { "Messenger" },
            ["www.facebook.com"] = new[] { "Facebook", "Messenger" },
            ["www.instagram.com"] = new[] { "Instagram" },
            ["x.com"] = new[] { " / X", "X" },
            ["www.linkedin.com"] = new[] { "LinkedIn" },
            ["www.youtube.com"] = new[] { "YouTube" }
        };

        private static readonly Regex DomainRegex = new(@"^(?:https?://)?([a-z0-9-]+(?:\.[a-z0-9-]+)+)/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Si el texto es un dominio (la "atribución" de la notificación), lo devuelve.</summary>
        public static string? AsDomain(string text)
        {
            var m = DomainRegex.Match(text.Trim());
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        public static string? BrowserProcess(string appId, string appName)
        {
            string id = (appId + " " + appName).ToLowerInvariant();
            foreach (var (key, process) in Browsers)
                if (id.Contains(key)) return process;
            return null;
        }

        public static bool IsBrowser(string appId, string appName) => BrowserProcess(appId, appName) != null;

        /// <summary>Busca la pestaña del sitio y la trae al frente; si no existe, abre el sitio.</summary>
        public static void FocusOrOpen(string appId, string appName, string site)
        {
            string? process = BrowserProcess(appId, appName);
            if (process == null) return;

            try
            {
                if (TryFocusTab(process, site)) return;
            }
            catch { /* UI Automation puede fallar con ventanas que se están cerrando */ }

            // No encontramos la pestaña: abrir el sitio en ese navegador
            try
            {
                Process.Start(new ProcessStartInfo(process + ".exe", "https://" + site) { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("https://" + site) { UseShellExecute = true }); } catch { }
            }
        }

        /// <summary>Trae al frente la ventana del navegador sin abrir pestañas nuevas.</summary>
        public static void FocusBrowser(string appId, string appName)
        {
            string? process = BrowserProcess(appId, appName);
            if (process == null) return;
            try
            {
                var hwnd = Process.GetProcessesByName(process)
                    .Select(p => p.MainWindowHandle)
                    .FirstOrDefault(h => h != IntPtr.Zero);
                if (hwnd != IntPtr.Zero) BringToFront(hwnd);
            }
            catch { }
        }

        private static IEnumerable<string> Keywords(string site)
        {
            if (KnownSites.TryGetValue(site, out var known)) return known;
            // Genérico: "app.ejemplo.com" → "ejemplo"
            var parts = site.Split('.');
            string label = parts.Length >= 2 ? parts[^2] : site;
            return new[] { label };
        }

        private static bool TryFocusTab(string process, string site)
        {
            var pids = Process.GetProcessesByName(process).Select(p => p.Id).ToHashSet();
            if (pids.Count == 0) return false;
            var keywords = Keywords(site).ToList();

            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement win in windows)
            {
                int pid;
                try { pid = win.Current.ProcessId; } catch { continue; }
                if (!pids.Contains(pid)) continue;

                var tabs = win.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                foreach (AutomationElement tab in tabs)
                {
                    string name;
                    try { name = tab.Current.Name ?? ""; } catch { continue; }
                    if (!keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

                    BringToFront(new IntPtr(win.Current.NativeWindowHandle));
                    SelectTab(tab);
                    return true;
                }
            }
            return false;
        }

        private static void SelectTab(AutomationElement tab)
        {
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
                ((SelectionItemPattern)sel).Select();
            else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                ((InvokePattern)inv).Invoke();
            else
                tab.SetFocus();
        }

        // ---------- Traer la ventana al frente ----------

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static void BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            // Windows solo deja cambiar el primer plano tras una tecla; un Alt "fantasma" lo permite
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
        }
    }
}
