using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>
    /// Formas de abrir la isla sin pasar el mouse por encima:
    /// atajo global (Ctrl+Alt+Espacio) y "Magic Shake" (agitar el mouse de lado a lado).
    /// </summary>
    public sealed class IslandTriggers : IDisposable
    {
        public event Action? HotkeyPressed;
        /// <summary>Se agitó el mouse; recibe el monitor (Screen.DeviceName) donde está el cursor.</summary>
        public event Action<string>? Shaken;

        public string HotkeyLabel { get; private set; } = "";
        public bool MagicShakeEnabled { get; set; } = true;

        private readonly MessageWindow _window = new("DynamicIsland.Hotkeys");
        private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(25) };

        private const int HotkeyId = 0xB101, WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000, VK_SPACE = 0x20;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        public void Start()
        {
            _window.Message += (msg, wParam, _) =>
            {
                if (msg != WM_HOTKEY || wParam.ToInt32() != HotkeyId) return false;
                HotkeyPressed?.Invoke();
                return true;
            };

            if (RegisterHotKey(_window.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE))
                HotkeyLabel = "Ctrl+Alt+Espacio";
            else if (RegisterHotKey(_window.Handle, HotkeyId, MOD_WIN | MOD_ALT | MOD_NOREPEAT, VK_SPACE))
                HotkeyLabel = "Win+Alt+Espacio";

            _poll.Tick += (_, _) => DetectShake();
            _poll.Start();
        }

        // ---------- Magic Shake ----------
        // Se detecta cuando el cursor cambia de dirección horizontal al menos 4 veces en ~0.6 s,
        // con recorridos amplios y sin botones presionados (para no confundirlo con arrastrar).

        private readonly List<(DateTime time, int x)> _turns = new();
        private int _lastX, _dir, _segmentStart;
        private bool _hasLast;
        private DateTime _cooldownUntil;

        private void DetectShake()
        {
            if (!MagicShakeEnabled || !GetCursorPos(out var p)) return;
            var now = DateTime.Now;

            bool buttons = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (buttons) { _turns.Clear(); _hasLast = false; return; }

            if (!_hasLast) { _lastX = _segmentStart = p.X; _hasLast = true; return; }

            int dx = p.X - _lastX;
            _lastX = p.X;
            if (Math.Abs(dx) < 3) return;

            int dir = Math.Sign(dx);
            if (dir != _dir)
            {
                // Cambio de sentido: cuenta solo si el tramo anterior fue amplio
                if (_dir != 0 && Math.Abs(p.X - dx - _segmentStart) >= 60) _turns.Add((now, p.X));
                _dir = dir;
                _segmentStart = p.X - dx;
            }

            _turns.RemoveAll(t => (now - t.time).TotalMilliseconds > 650);
            if (_turns.Count >= 4 && now >= _cooldownUntil)
            {
                _turns.Clear();
                _cooldownUntil = now.AddSeconds(1.5);
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
                Shaken?.Invoke(screen.DeviceName);
            }
        }

        public void Dispose()
        {
            _poll.Stop();
            UnregisterHotKey(_window.Handle, HotkeyId);
            _window.Dispose();
        }
    }
}
