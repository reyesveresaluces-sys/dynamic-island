using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DynamicIsland
{
    /// <summary>
    /// La animación de "absorber" una ventana hacia la isla (y la inversa al traerla de vuelta):
    /// una capa transparente que no recibe clics, con la captura de la ventana encogiéndose por una curva.
    /// </summary>
    public sealed class StashAnimation : Window
    {
        private readonly Image _img;
        private readonly Canvas _canvas = new();
        private readonly Rect _from, _to;
        private readonly bool _reverse;

        /// <summary>Lanza la animación; si algo falla, simplemente no se dibuja.</summary>
        public static void Play(ImageSource shot, Rect from, Rect to, bool reverse)
        {
            try
            {
                if (shot == null || from.Width < 2 || to.Width < 2) return;
                new StashAnimation(shot, from, to, reverse).Go();
            }
            catch { }
        }

        private StashAnimation(ImageSource shot, Rect from, Rect to, bool reverse)
        {
            _from = from; _to = to; _reverse = reverse;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Title = "";
            Content = _canvas;

            _img = new Image
            {
                Source = shot,
                Stretch = Stretch.Fill,
                Opacity = reverse ? 0.35 : 1,
                Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black }
            };
            _canvas.Children.Add(_img);

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
            };
        }

        private Rect _area;

        private void Go()
        {
            _area = Rect.Union(_from, _to);
            _area.Inflate(12, 12);

            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, (int)_area.X, (int)_area.Y, (int)_area.Width, (int)_area.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

            Dispatcher.BeginInvoke(new Action(Animate), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Animate()
        {
            // Píxeles físicos → unidades de WPF en este monitor
            var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            double sx = m.M11, sy = m.M22;
            Rect f = Local(_from, sx, sy), t = Local(_to, sx, sy);

            _img.Width = f.Width;
            _img.Height = f.Height;
            Canvas.SetLeft(_img, f.X);
            Canvas.SetTop(_img, f.Y);

            var dur = TimeSpan.FromMilliseconds(_reverse ? 320 : 400);
            var mode = _reverse ? EasingMode.EaseOut : EasingMode.EaseIn;

            // X e Y con curvas distintas: el recorrido sale curvo, no recto
            Animate(Canvas.LeftProperty, f.X, t.X, dur, new CubicEase { EasingMode = EasingMode.EaseInOut });
            Animate(Canvas.TopProperty, f.Y, t.Y, dur, new QuarticEase { EasingMode = mode });
            Animate(WidthProperty, f.Width, t.Width, dur, new CubicEase { EasingMode = mode });

            var last = new DoubleAnimation(f.Height, t.Height, dur) { EasingFunction = new CubicEase { EasingMode = mode } };
            last.Completed += (_, _) => { try { Close(); } catch { } };
            _img.BeginAnimation(HeightProperty, last);

            _img.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_reverse ? 0.35 : 1, _reverse ? 1 : 0.15, dur)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }

        private void Animate(DependencyProperty prop, double from, double to, TimeSpan dur, IEasingFunction ease) =>
            _img.BeginAnimation(prop, new DoubleAnimation(from, to, dur) { EasingFunction = ease });

        private Rect Local(Rect physical, double sx, double sy) =>
            new((physical.X - _area.X) * sx, (physical.Y - _area.Y) * sy, physical.Width * sx, physical.Height * sy);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
        private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
