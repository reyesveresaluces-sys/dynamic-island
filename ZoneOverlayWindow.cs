using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    /// <summary>
    /// Capa transparente que cubre un monitor y dibuja las zonas mientras se arrastra una ventana.
    /// No recibe clics ni foco (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE), así que el arrastre sigue normal.
    /// </summary>
    public sealed class ZoneOverlayWindow : Window
    {
        private readonly Canvas _canvas = new();
        private readonly List<(Border box, TextBlock label)> _items = new();
        private string _layoutKey = "";
        private int _zoneCount;
        private bool _shown;

        private static readonly Brush ZoneFill = Frozen(Color.FromArgb(0x55, 0x0A, 0x84, 0xFF));
        private static readonly Brush ZoneFillActive = Frozen(Color.FromArgb(0xA8, 0x0A, 0x84, 0xFF));
        private static readonly Brush ZoneStroke = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        private static readonly Brush ZoneStrokeActive = Frozen(Colors.White);

        public ZoneOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Title = "Zonas";
            Content = _canvas;
            Opacity = 0;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED));
            };
        }

        /// <summary>Muestra las zonas (píxeles físicos) del monitor y resalta las seleccionadas.</summary>
        public void ShowZones(Forms.Screen screen, List<Rect> zones, List<int> selected)
        {
            var b = screen.Bounds;
            if (!IsVisible) { Show(); }
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

            // Conversión de píxeles físicos a unidades de WPF según la escala de este monitor
            var src = PresentationSource.FromVisual(this);
            var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            double sx = m.M11, sy = m.M22;

            string key = $"{screen.DeviceName}|{zones.Count}|{(zones.Count > 0 ? zones[0] : Rect.Empty)}|{sx}";
            if (key != _layoutKey || _zoneCount != zones.Count)
            {
                _layoutKey = key;
                _zoneCount = zones.Count;
                Rebuild(zones, b, sx, sy);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                bool on = selected.Contains(i);
                _items[i].box.Background = on ? ZoneFillActive : ZoneFill;
                _items[i].box.BorderBrush = on ? ZoneStrokeActive : ZoneStroke;
                _items[i].box.BorderThickness = new Thickness(on ? 2 : 1);
                _items[i].label.Opacity = on ? 1 : 0.8;
            }

            if (!_shown)
            {
                _shown = true;
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            }
        }

        private void Rebuild(List<Rect> zones, System.Drawing.Rectangle bounds, double sx, double sy)
        {
            _canvas.Children.Clear();
            _items.Clear();
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                var box = new Border
                {
                    Width = z.Width * sx,
                    Height = z.Height * sy,
                    CornerRadius = new CornerRadius(12),
                    Background = ZoneFill,
                    BorderBrush = ZoneStroke,
                    BorderThickness = new Thickness(1)
                };
                var label = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = Math.Clamp(Math.Min(z.Width, z.Height) * sx * 0.18, 28, 110),
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.8
                };
                label.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 };
                box.Child = label;
                Canvas.SetLeft(box, (z.X - bounds.X) * sx);
                Canvas.SetTop(box, (z.Y - bounds.Y) * sy);
                _canvas.Children.Add(box);
                _items.Add((box, label));
            }
        }

        public void HideZones()
        {
            if (!_shown) return;
            _shown = false;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            a.Completed += (_, _) => { if (!_shown) Hide(); };
            BeginAnimation(OpacityProperty, a);
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
        private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
