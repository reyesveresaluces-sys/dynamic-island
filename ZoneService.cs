using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    /// <summary>
    /// Zonas de ventanas estilo FancyZones (PowerToys):
    /// - cada monitor tiene su disposición (columnas, cuadrícula, prioridad…);
    /// - al arrastrar una ventana con Shift aparecen las zonas y, al soltarla, se ajusta a la zona señalada
    ///   (con Ctrl se pueden abarcar varias zonas);
    /// - Ctrl+Alt+←/→ mueve la ventana activa de zona en zona; desde la isla se manda a una zona con un clic.
    /// </summary>
    public sealed class ZoneService : IDisposable
    {
        public event Action? Changed;

        private bool _enabled = true, _requireShift = true;

        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled == value) return; _enabled = value; if (!value) { _dragTimer.Stop(); HideOverlay(); } Changed?.Invoke(); }
        }

        /// <summary>Si es verdadero, las zonas solo aparecen al arrastrar con Shift; si no, siempre.</summary>
        public bool RequireShift
        {
            get => _requireShift;
            set { if (_requireShift == value) return; _requireShift = value; Changed?.Invoke(); }
        }

        public int Spacing { get; set; } = 10;
        public string HotkeyLabel { get; private set; } = "";

        private readonly Dictionary<string, ZoneOverlayWindow> _overlays = new();
        private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private readonly MessageWindow _window = new("DynamicIsland.Zones");

        // Ventana que se está arrastrando
        private IntPtr _dragging;
        private RECT _dragStartRect, _lastRect;
        private string? _overlayScreen;
        private readonly List<int> _selected = new();
        private bool _spanning;

        private IntPtr _lastForeign;              // última ventana activa que no es nuestra
        private readonly int _ownPid = Environment.ProcessId;

        // ---------------- Disposiciones ----------------

        public ZoneLayout LayoutFor(string screenId)
        {
            AppSettings.Current.ZoneLayoutByScreen.TryGetValue(screenId, out var key);
            return ZoneLayouts.Get(key);
        }

        public void SetLayout(string screenId, string key)
        {
            AppSettings.Current.ZoneLayoutByScreen[screenId] = key;
            AppSettings.Current.Save();
            Changed?.Invoke();
        }

        public void SetSpacing(int spacing)
        {
            Spacing = Math.Clamp(spacing, 0, 40);
            AppSettings.Current.ZoneSpacing = Spacing;
            AppSettings.Current.Save();
            Changed?.Invoke();
        }

        /// <summary>Guarda un diseño del editor y lo aplica a esa pantalla.</summary>
        public void SaveCustom(CustomZoneLayout layout, string screenId)
        {
            var list = AppSettings.Current.CustomLayouts;
            int i = list.FindIndex(c => c.Id == layout.Id);
            if (i >= 0) list[i] = layout; else list.Add(layout);
            AppSettings.Current.ZoneLayoutByScreen[screenId] = layout.Key;
            AppSettings.Current.Save();
            Changed?.Invoke();
        }

        /// <summary>Borra un diseño propio; las pantallas que lo usaban vuelven a la disposición normal.</summary>
        public void DeleteCustom(string id)
        {
            var list = AppSettings.Current.CustomLayouts;
            list.RemoveAll(c => c.Id == id);
            string key = "custom:" + id;
            foreach (var screen in AppSettings.Current.ZoneLayoutByScreen.Where(p => p.Value == key).Select(p => p.Key).ToList())
                AppSettings.Current.ZoneLayoutByScreen[screen] = ZoneLayouts.DefaultKey;
            AppSettings.Current.Save();
            Changed?.Invoke();
        }

        private ZoneEditorWindow? _editor;

        /// <summary>El editor de zonas está abierto (la isla no debe taparlo).</summary>
        public bool EditorOpen => _editor != null;

        /// <summary>Abre el editor de zonas sobre esa pantalla (parte del diseño que tenga puesto, o del que se indique).</summary>
        public void OpenEditor(string screenId, string? editKey = null)
        {
            var screen = ScreenById(screenId) ?? Forms.Screen.PrimaryScreen;
            if (screen == null) return;
            if (_editor != null) { _editor.Activate(); return; }

            var layout = ZoneLayouts.Get(editKey ?? LayoutFor(screenId).Key);
            string? id = layout.Key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) ? layout.Key[7..] : null;
            string name = id != null ? layout.Name : "Mi diseño";

            var editor = new ZoneEditorWindow(screen, layout.Zones, name, id);
            _editor = editor;
            editor.Closed += (_, _) => _editor = null;
            editor.Saved += l => SaveCustom(l, screenId);
            editor.Show();
            editor.Activate();
        }

        public static Forms.Screen? ScreenById(string id)
        {
            foreach (var s in Forms.Screen.AllScreens) if (s.DeviceName == id) return s;
            return null;
        }

        /// <summary>Zonas en píxeles físicos del monitor.</summary>
        public List<Rect> ZonesFor(string screenId)
        {
            var screen = ScreenById(screenId) ?? Forms.Screen.PrimaryScreen;
            if (screen == null) return new List<Rect>();
            var a = screen.WorkingArea;
            return LayoutFor(screenId).Resolve(new Rect(a.X, a.Y, a.Width, a.Height), Spacing);
        }

        // ---------------- Arranque ----------------

        private WinEventDelegate? _proc;
        private readonly List<IntPtr> _hooks = new();

        public void Start()
        {
            _enabled = AppSettings.Current.ZonesEnabled;
            _requireShift = AppSettings.Current.ZonesRequireShift;
            Spacing = Math.Clamp(AppSettings.Current.ZoneSpacing, 0, 40);

            _proc = OnWinEvent;
            _hooks.Add(SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
            _hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
            _dragTimer.Tick += (_, _) => DragTick();

            _window.Message += (msg, wParam, _) =>
            {
                if (msg != WM_HOTKEY) return false;
                int id = wParam.ToInt32();
                if (id == HotkeyLeft) MoveForeground(-1);
                else if (id == HotkeyRight) MoveForeground(1);
                else if (id == HotkeyUp) SnapForegroundToZone(0);
                else return false;
                return true;
            };
            RegisterHotkeys();
        }

        private const int HotkeyLeft = 0xB201, HotkeyRight = 0xB202, HotkeyUp = 0xB203;

        private void RegisterHotkeys()
        {
            foreach (var (mods, label) in new[] { (MOD_CONTROL | MOD_ALT, "Ctrl+Alt"), (MOD_CONTROL | MOD_WIN | MOD_ALT, "Ctrl+Win+Alt") })
            {
                bool l = RegisterHotKey(_window.Handle, HotkeyLeft, mods | MOD_NOREPEAT, VK_LEFT);
                bool r = RegisterHotKey(_window.Handle, HotkeyRight, mods | MOD_NOREPEAT, VK_RIGHT);
                if (l && r)
                {
                    RegisterHotKey(_window.Handle, HotkeyUp, mods | MOD_NOREPEAT, VK_UP);
                    HotkeyLabel = label + "+←/→";
                    return;
                }
                if (l) UnregisterHotKey(_window.Handle, HotkeyLeft);
                if (r) UnregisterHotKey(_window.Handle, HotkeyRight);
            }
        }

        // ---------------- Arrastre de ventanas ----------------

        private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            if (evt == EVENT_SYSTEM_FOREGROUND)
            {
                if (IsCandidate(hwnd)) _lastForeign = hwnd;
                return;
            }
            if (evt == EVENT_SYSTEM_MOVESIZEEND)
            {
                if (hwnd != _dragging) return;
                _dragTimer.Stop();
                if (Enabled) FinishDrag();
                else { _dragging = IntPtr.Zero; _selected.Clear(); HideOverlay(); }
                return;
            }
            if (!Enabled) return;
            if (evt == EVENT_SYSTEM_MOVESIZESTART)
            {
                if (!IsCandidate(hwnd)) return;
                _dragging = hwnd;
                GetWindowRect(hwnd, out _dragStartRect);
                _lastRect = _dragStartRect;
                _selected.Clear();
                _spanning = false;
                _dragTimer.Start();
            }
        }

        private void DragTick()
        {
            if (!Enabled || _dragging == IntPtr.Zero || !GetCursorPos(out var p)) { HideOverlay(); return; }

            // Si el tamaño sigue cambiando, es un redimensionado y no un arrastre.
            // Se compara con el tick anterior (no con el inicio) para que una ventana maximizada,
            // que cambia de tamaño una sola vez al despegarla, sí muestre las zonas.
            if (GetWindowRect(_dragging, out var now))
            {
                bool resizing = now.Right - now.Left != _lastRect.Right - _lastRect.Left
                             || now.Bottom - now.Top != _lastRect.Bottom - _lastRect.Top;
                _lastRect = now;
                if (resizing) { _selected.Clear(); HideOverlay(); return; }
            }

            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool show = RequireShift ? shift : !shift; // sin Shift obligatorio, Shift lo desactiva
            if (!show) { _selected.Clear(); HideOverlay(); return; }

            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
            var zones = ZonesFor(screen.DeviceName);
            // Si hay zonas superpuestas, gana la más pequeña que contiene al cursor
            int hit = -1;
            double hitArea = double.MaxValue;
            for (int i = 0; i < zones.Count; i++)
            {
                if (!zones[i].Contains(p.X, p.Y)) continue;
                double area = zones[i].Width * zones[i].Height;
                if (area < hitArea) { hitArea = area; hit = i; }
            }

            // Si el cursor cayó justo en la separación entre zonas, vale la más cercana
            if (hit < 0)
            {
                double best = 80 * 80;
                for (int i = 0; i < zones.Count; i++)
                {
                    var z = zones[i];
                    double dx = Math.Max(Math.Max(z.X - p.X, p.X - z.Right), 0);
                    double dy = Math.Max(Math.Max(z.Y - p.Y, p.Y - z.Bottom), 0);
                    double d = dx * dx + dy * dy;
                    if (d < best) { best = d; hit = i; }
                }
            }

            if (_overlayScreen != screen.DeviceName) { _selected.Clear(); _spanning = false; }

            if (ctrl)
            {
                if (!_spanning) { _spanning = true; _selected.Clear(); }
                if (hit >= 0 && !_selected.Contains(hit)) _selected.Add(hit);
            }
            else
            {
                _spanning = false;
                _selected.Clear();
                if (hit >= 0) _selected.Add(hit);
            }

            ShowOverlay(screen, zones, _selected);
        }

        private void FinishDrag()
        {
            var hwnd = _dragging;
            _dragging = IntPtr.Zero;
            var selected = _selected.ToList();
            var screenId = _overlayScreen;
            HideOverlay();
            if (hwnd == IntPtr.Zero || selected.Count == 0 || screenId == null) return;

            // Si la ventana quedó exactamente donde empezó, el arrastre se canceló (Esc): no se ajusta
            if (GetWindowRect(hwnd, out var end)
                && end.Left == _dragStartRect.Left && end.Top == _dragStartRect.Top
                && end.Right == _dragStartRect.Right && end.Bottom == _dragStartRect.Bottom) return;

            var zones = ZonesFor(screenId);
            Rect target = Rect.Empty;
            foreach (int i in selected) if (i < zones.Count) target.Union(zones[i]);
            if (!target.IsEmpty) SnapWindow(hwnd, target);
        }

        private void ShowOverlay(Forms.Screen screen, List<Rect> zones, List<int> selected)
        {
            if (_overlayScreen != null && _overlayScreen != screen.DeviceName) HideOverlay();
            _overlayScreen = screen.DeviceName;
            if (!_overlays.TryGetValue(screen.DeviceName, out var overlay))
            {
                overlay = new ZoneOverlayWindow();
                _overlays[screen.DeviceName] = overlay;
            }
            overlay.ShowZones(screen, zones, selected);
        }

        private void HideOverlay()
        {
            if (_overlayScreen == null) return;
            if (_overlays.TryGetValue(_overlayScreen, out var o)) o.HideZones();
            _overlayScreen = null;
        }

        // ---------------- Mover ventanas ----------------

        /// <summary>Ventana a la que se aplican las acciones desde la isla o los atajos.</summary>
        private IntPtr TargetWindow()
        {
            var fg = GetForegroundWindow();
            if (IsCandidate(fg)) return fg;
            return IsCandidate(_lastForeign) ? _lastForeign : IntPtr.Zero;
        }

        /// <summary>Manda la ventana activa a la zona indicada del monitor donde está (o del indicado).</summary>
        public bool SnapForegroundToZone(int index, string? screenId = null)
        {
            var hwnd = TargetWindow();
            if (hwnd == IntPtr.Zero) return false;
            screenId ??= Forms.Screen.FromHandle(hwnd).DeviceName;
            var zones = ZonesFor(screenId);
            if (zones.Count == 0) return false;
            SnapWindow(hwnd, zones[Math.Clamp(index, 0, zones.Count - 1)]);
            return true;
        }

        /// <summary>Mueve la ventana activa a la zona anterior/siguiente (pasa de monitor al llegar al borde).</summary>
        public void MoveForeground(int direction)
        {
            var hwnd = TargetWindow();
            if (hwnd == IntPtr.Zero) return;

            var screens = Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
            var screen = Forms.Screen.FromHandle(hwnd);
            int si = screens.FindIndex(s => s.DeviceName == screen.DeviceName);
            if (si < 0) si = 0;
            var zones = ZonesFor(screens[si].DeviceName);
            if (zones.Count == 0) return;

            // Zona actual: la que más se parece a la ventana
            var cur = VisibleRect(hwnd);
            int best = -1; double bestScore = double.MaxValue;
            for (int i = 0; i < zones.Count; i++)
            {
                double d = Math.Abs(zones[i].X - cur.X) + Math.Abs(zones[i].Y - cur.Y) + Math.Abs(zones[i].Width - cur.Width) + Math.Abs(zones[i].Height - cur.Height);
                if (d < bestScore) { bestScore = d; best = i; }
            }
            bool inZone = bestScore < 40;
            int next = inZone ? best + direction : (direction > 0 ? 0 : zones.Count - 1);

            if (next < 0 || next >= zones.Count)
            {
                if (screens.Count > 1)
                {
                    int ns = (si + direction + screens.Count) % screens.Count;
                    var nz = ZonesFor(screens[ns].DeviceName);
                    if (nz.Count > 0) { SnapWindow(hwnd, direction > 0 ? nz[0] : nz[^1]); return; }
                }
                next = (next + zones.Count) % zones.Count;
            }
            SnapWindow(hwnd, zones[next]);
        }

        /// <summary>Coloca la ventana en el rectángulo (píxeles físicos), compensando el marco invisible de Windows.</summary>
        public void SnapWindow(IntPtr hwnd, Rect target)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

                // GetWindowRect incluye un borde invisible; DWM da el rectángulo que se ve
                GetWindowRect(hwnd, out var wr);
                int l = wr.Left, t = wr.Top, r = wr.Right, b = wr.Bottom;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) == 0)
                {
                    l = wr.Left - fr.Left; t = wr.Top - fr.Top; r = wr.Right - fr.Right; b = wr.Bottom - fr.Bottom;
                }
                else l = t = r = b = 0;

                int x = (int)Math.Round(target.X) + l;
                int y = (int)Math.Round(target.Y) + t;
                int w = (int)Math.Round(target.Width) - l + r;
                int h = (int)Math.Round(target.Height) - t + b;
                SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                // Algunas apps (con marco propio) necesitan un segundo ajuste al cambiar de monitor/escala
                var vis = VisibleRect(hwnd);
                if (Math.Abs(vis.X - target.X) > 2 || Math.Abs(vis.Y - target.Y) > 2 || Math.Abs(vis.Width - target.Width) > 2 || Math.Abs(vis.Height - target.Height) > 2)
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }

        private static Rect VisibleRect(IntPtr hwnd)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) == 0)
                return new Rect(fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
            GetWindowRect(hwnd, out var wr);
            return new Rect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
        }

        /// <summary>Ventanas normales de otras apps (con título, visibles, no herramientas ni ocultas).</summary>
        private bool IsCandidate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return false;

            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if ((style & WS_CHILD) != 0) return false;
            if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0) return false;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
            return true;
        }

        public void Dispose()
        {
            _dragTimer.Stop();
            foreach (var h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
            foreach (var id in new[] { HotkeyLeft, HotkeyRight, HotkeyUp }) UnregisterHotKey(_window.Handle, id);
            foreach (var o in _overlays.Values) o.Close();
            _editor?.Close();
            _window.Dispose();
        }

        // ---------------- Win32 ----------------

        private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int OBJID_WINDOW = 0;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        private const uint VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const long WS_CHILD = 0x40000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint idProcess, uint idThread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    }
}
