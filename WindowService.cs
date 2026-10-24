using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    /// <summary>Una ventana abierta de otra app, lista para mostrarse en la isla.</summary>
    public sealed class OpenWindow
    {
        public IntPtr Handle { get; init; }
        public string Title { get; init; } = "";
        public string AppName { get; init; } = "";
        public ImageSource? Icon { get; init; }
        public ImageSource? Shot { get; init; }
        public bool Minimized { get; init; }
        public bool Stashed { get; init; }

        public string Headline => Title.Length > 0 ? Title : AppName;
        public Visibility ShotVisibility => Shot is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility NoShotVisibility => Shot is null ? Visibility.Visible : Visibility.Collapsed;
        public double CardOpacity => Minimized ? 0.75 : 1.0;

        /// <summary>Color estable por app para las tarjetas sin captura.</summary>
        public Brush AccentBrush
        {
            get
            {
                string[] palette = { "#FF5E5CE6", "#FF0A84FF", "#FF30D158", "#FFFF9F0A", "#FFFF375F", "#FF64D2FF", "#FFBF5AF2" };
                int h = 0;
                foreach (char c in AppName) h = (h * 31 + c) & 0x7fffffff;
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[h % palette.Length]));
                b.Freeze();
                return b;
            }
        }
    }

    /// <summary>
    /// Ventanas abiertas: lista, íconos, capturas, traer al frente y "guardar en la isla"
    /// (se absorbe con una animación y queda minimizada, lista para volver con un clic).
    /// </summary>
    public sealed class WindowService : IDisposable
    {
        public event Action? Changed;

        public string HotkeyLabel { get; private set; } = "";

        private readonly MessageWindow _window = new("DynamicIsland.Windows");
        private readonly Dictionary<IntPtr, ImageSource?> _shots = new();
        private readonly Dictionary<IntPtr, DateTime> _shotTime = new();
        private readonly Dictionary<uint, (string name, ImageSource? icon)> _apps = new();
        private readonly List<IntPtr> _stashed = new();
        private readonly int _ownPid = Environment.ProcessId;

        private const int HotkeyStash = 0xB301;

        private WinEventDelegate? _proc;
        private IntPtr _hook, _lastForeign;

        public void Start()
        {
            // La última ventana activa que no es nuestra: es la que se guarda con el atajo
            _proc = (_, _, hwnd, idObject, _, _, _) =>
            {
                if (idObject == 0 && IsAppWindow(hwnd)) _lastForeign = hwnd;
            };
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _window.Message += (msg, wParam, _) =>
            {
                if (msg != WM_HOTKEY || wParam.ToInt32() != HotkeyStash) return false;
                StashForeground();
                return true;
            };

            if (RegisterHotKey(_window.Handle, HotkeyStash, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_DOWN))
                HotkeyLabel = "Ctrl+Alt+↓";
            else if (RegisterHotKey(_window.Handle, HotkeyStash, MOD_CONTROL | MOD_WIN | MOD_ALT | MOD_NOREPEAT, VK_DOWN))
                HotkeyLabel = "Ctrl+Win+Alt+↓";
        }

        // ---------------- Lista ----------------

        /// <summary>Ventanas normales de otras apps; primero las guardadas en la isla.</summary>
        public List<OpenWindow> List(bool withShots = true)
        {
            var found = new List<IntPtr>();
            EnumWindows((h, _) => { if (IsAppWindow(h)) found.Add(h); return true; }, IntPtr.Zero);

            _stashed.RemoveAll(h => !IsWindow(h));
            foreach (var dead in _shots.Keys.Where(h => !IsWindow(h)).ToList())
            {
                _shots.Remove(dead);
                _shotTime.Remove(dead);
            }

            var list = new List<OpenWindow>();
            foreach (var h in found)
            {
                GetWindowThreadProcessId(h, out uint pid);
                var (app, icon) = AppOf(pid, h);
                bool min = IsIconic(h);
                list.Add(new OpenWindow
                {
                    Handle = h,
                    Title = TitleOf(h),
                    AppName = app,
                    Icon = icon,
                    Minimized = min,
                    Stashed = _stashed.Contains(h),
                    Shot = withShots ? Shot(h, min) : null
                });
            }

            return list
                .OrderByDescending(w => w.Stashed)
                .ThenBy(w => { int i = _stashed.IndexOf(w.Handle); return i >= 0 ? i : int.MaxValue; })
                .ThenBy(w => w.AppName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Cuántas ventanas hay guardadas en la isla.</summary>
        public int StashedCount => _stashed.Where(IsWindow).Count();

        private static string TitleOf(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Nombre bonito e ícono de la app dueña de la ventana (con caché por proceso).</summary>
        private (string name, ImageSource? icon) AppOf(uint pid, IntPtr hwnd)
        {
            if (_apps.TryGetValue(pid, out var cached)) return cached;

            string name = "Aplicación";
            ImageSource? icon = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                string? exe = null;
                try { exe = p.MainModule?.FileName; } catch { }
                if (!string.IsNullOrEmpty(exe))
                {
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(exe);
                        if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription!;
                        else name = System.IO.Path.GetFileNameWithoutExtension(exe);
                    }
                    catch { name = System.IO.Path.GetFileNameWithoutExtension(exe); }
                    icon = ShellIcons.Get(exe);
                }
                else name = p.ProcessName;
            }
            catch { }

            icon ??= IconOf(hwnd);
            var pair = (name, icon);
            _apps[pid] = pair;
            return pair;
        }

        private static ImageSource? IconOf(IntPtr hwnd)
        {
            try
            {
                IntPtr h = SendMessageResult(hwnd, ICON_BIG);
                if (h == IntPtr.Zero) h = SendMessageResult(hwnd, ICON_SMALL2);
                if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICON);
                if (h == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        private static IntPtr SendMessageResult(IntPtr hwnd, int type)
        {
            SendMessageTimeout(hwnd, WM_GETICON, type, 0, SMTO_ABORTIFHUNG, 200, out IntPtr result);
            return result;
        }

        // ---------------- Capturas ----------------

        /// <summary>Captura de la ventana (se reutiliza unos segundos; las minimizadas usan la última que haya).</summary>
        public ImageSource? Shot(IntPtr hwnd, bool minimized)
        {
            if (minimized)
                return _shots.TryGetValue(hwnd, out var old) ? old : null;

            if (_shots.TryGetValue(hwnd, out var cached)
                && _shotTime.TryGetValue(hwnd, out var when)
                && (DateTime.UtcNow - when).TotalSeconds < 4)
                return cached;

            var shot = Capture(hwnd);
            _shots[hwnd] = shot;
            _shotTime[hwnd] = DateTime.UtcNow;
            return shot;
        }

        /// <summary>Imagen de la ventana tal como se ve (PrintWindow con el contenido completo).</summary>
        public static ImageSource? Capture(IntPtr hwnd, int maxWidth = 360)
        {
            try
            {
                if (!GetWindowRect(hwnd, out var r)) return null;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 40 || h < 40 || w > 20000 || h > 20000) return null;

                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);
                    if (!ok) return null;
                }

                int tw = Math.Min(maxWidth, w);
                int th = Math.Max(1, (int)((long)h * tw / w));
                using var small = new System.Drawing.Bitmap(bmp, new System.Drawing.Size(tw, th));
                return FromBitmap(small);
            }
            catch { return null; }
        }

        private static ImageSource? FromBitmap(System.Drawing.Bitmap bmp)
        {
            IntPtr hbmp = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DeleteObject(hbmp); }
        }

        // ---------------- Acciones ----------------

        /// <summary>Trae la ventana al frente (y la restaura si estaba minimizada), con animación si venía guardada.</summary>
        public void Activate(IntPtr hwnd, bool animate = true)
        {
            if (!IsWindow(hwnd)) return;
            bool wasMin = IsIconic(hwnd);
            _stashed.Remove(hwnd);

            AllowSetForegroundWindow(-1);
            if (wasMin && animate && _shots.TryGetValue(hwnd, out var shot) && shot != null)
                StashAnimation.Play(shot, IslandRect(hwnd), RestoredRect(hwnd), reverse: true);

            if (wasMin) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            Changed?.Invoke();
        }

        /// <summary>Guarda la ventana activa en la isla (si la isla tiene el foco, la anterior).</summary>
        public void StashForeground()
        {
            var fg = GetForegroundWindow();
            Stash(IsAppWindow(fg) ? fg : _lastForeign);
        }

        /// <summary>Anima la ventana hacia la isla y la minimiza.</summary>
        public void Stash(IntPtr hwnd)
        {
            if (!IsAppWindow(hwnd) || IsIconic(hwnd)) return;

            var shot = Capture(hwnd);
            if (shot != null)
            {
                _shots[hwnd] = shot;
                _shotTime[hwnd] = DateTime.UtcNow;
            }

            if (!_stashed.Contains(hwnd)) _stashed.Insert(0, hwnd);

            if (shot != null && GetWindowRect(hwnd, out var r))
                StashAnimation.Play(shot, ToRect(r), IslandRect(hwnd), reverse: false);

            ShowWindow(hwnd, SW_MINIMIZE);
            Changed?.Invoke();
        }

        /// <summary>Punto de destino: la isla del monitor donde está la ventana.</summary>
        private static Rect IslandRect(IntPtr hwnd)
        {
            var screen = Forms.Screen.FromHandle(hwnd);
            var a = screen.WorkingArea;
            const double w = 150, h = 34;
            return new Rect(a.X + (a.Width - w) / 2, a.Y + 4, w, h);
        }

        private static Rect ToRect(RECT r) => new(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));

        /// <summary>Rectángulo que tendrá la ventana al restaurarse (la minimizada no tiene uno válido).</summary>
        private static Rect RestoredRect(IntPtr hwnd)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp)) return ToRect(wp.rcNormalPosition);
            return GetWindowRect(hwnd, out var r) ? ToRect(r) : new Rect(100, 100, 900, 600);
        }

        /// <summary>Ventanas normales de otras apps (con título, visibles, no herramientas ni ocultas).</summary>
        public bool IsAppWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return false;
            if (GetWindowTextLength(hwnd) == 0) return false;

            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if ((style & WS_CHILD) != 0) return false;
            if ((style & WS_VISIBLE) == 0) return false;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow") return false;
            return true;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            UnregisterHotKey(_window.Handle, HotkeyStash);
            _window.Dispose();
        }

        // ---------------- Win32 ----------------

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT ptMinPosition, ptMaxPosition;
            public RECT rcNormalPosition;
        }

        private const int WM_HOTKEY = 0x0312, WM_GETICON = 0x007F;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        private const uint VK_DOWN = 0x28;
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GCLP_HICON = -14;
        private const long WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_CLOAKED = 14;
        private const int SW_RESTORE = 9, SW_MINIMIZE = 6;
        private const int ICON_BIG = 1, ICON_SMALL2 = 2;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint GW_OWNER = 4;
        private const int PW_RENDERFULLCONTENT = 2;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, int wParam, int lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, int flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    }
}
