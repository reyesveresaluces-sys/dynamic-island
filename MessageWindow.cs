using System;
using System.Windows.Interop;

namespace DynamicIsland
{
    /// <summary>Ventana invisible solo para mensajes (portapapeles, atajos globales).</summary>
    public sealed class MessageWindow : IDisposable
    {
        public delegate bool MessageHandler(int msg, IntPtr wParam, IntPtr lParam);
        public event MessageHandler? Message;

        private readonly HwndSource _source;
        public IntPtr Handle => _source.Handle;

        public MessageWindow(string name)
        {
            var p = new HwndSourceParameters(name)
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            };
            _source = new HwndSource(p);
            _source.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (Message != null)
                foreach (MessageHandler h in Message.GetInvocationList())
                    if (h(msg, wParam, lParam)) { handled = true; break; }
            return IntPtr.Zero;
        }

        public void Dispose() => _source.Dispose();
    }
}
