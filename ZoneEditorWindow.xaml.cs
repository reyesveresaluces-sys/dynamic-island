using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    /// <summary>
    /// Editor de zonas a mano alzada (como el editor de diseños de FancyZones):
    /// cubre el monitor y permite crear, mover y redimensionar zonas, que pueden superponerse.
    /// Al guardar devuelve el diseño en fracciones 0–1 del área de trabajo.
    /// </summary>
    public partial class ZoneEditorWindow : Window
    {
        /// <summary>Se guardó el diseño.</summary>
        public event Action<CustomZoneLayout>? Saved;

        private readonly Forms.Screen _screen;
        private readonly IReadOnlyList<Rect> _initial;
        private readonly string _id;

        private readonly List<Rect> _zones = new();   // en unidades del lienzo
        private readonly List<Grid> _visuals = new();
        private bool _built;

        private const double MinW = 120, MinH = 90, SnapDistance = 14;

        private static readonly Brush ZoneFill = Frozen(Color.FromArgb(0x4D, 0x0A, 0x84, 0xFF));
        private static readonly Brush ZoneFillHot = Frozen(Color.FromArgb(0x80, 0x0A, 0x84, 0xFF));
        private static readonly Brush ZoneStroke = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        private static readonly Brush GripFill = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

        public ZoneEditorWindow(Forms.Screen screen, IReadOnlyList<Rect> fractions, string name, string? id)
        {
            InitializeComponent();
            _screen = screen;
            _initial = fractions;
            _id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N")[..8] : id!;
            NameBox.Text = string.IsNullOrWhiteSpace(name) ? "Mi diseño" : name;

            Loaded += (_, _) => PositionOverScreen();
            Board.SizeChanged += (_, _) => Build();
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            KeyDown += OnKeyDown;
        }

        // ---------------- Montaje ----------------

        private void PositionOverScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var a = _screen.WorkingArea;
            SetWindowPos(hwnd, IntPtr.Zero, a.X, a.Y, a.Width, a.Height, SWP_NOZORDER | SWP_SHOWWINDOW);
        }

        private void Build()
        {
            if (_built || Board.ActualWidth < 80 || Board.ActualHeight < 80) return;
            _built = true;

            double w = Board.ActualWidth, h = Board.ActualHeight;
            foreach (var f in _initial)
                _zones.Add(new Rect(f.X * w, f.Y * h, Math.Max(MinW, f.Width * w), Math.Max(MinH, f.Height * h)));
            if (_zones.Count == 0) _zones.Add(new Rect(w * 0.2, h * 0.2, w * 0.6, h * 0.6));

            Render();
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void Render()
        {
            Board.Children.Clear();
            _visuals.Clear();
            for (int i = 0; i < _zones.Count; i++)
            {
                var v = CreateVisual(i);
                Board.Children.Add(v);
                _visuals.Add(v);
                Place(i);
            }
        }

        private Grid CreateVisual(int index)
        {
            var g = new Grid { Tag = index, Cursor = Cursors.SizeAll };

            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = ZoneFill,
                BorderBrush = ZoneStroke,
                BorderThickness = new Thickness(2)
            });

            g.Children.Add(new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 44,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });

            var del = new Button
            {
                Style = (Style)FindResource("CloseButton"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                ToolTip = "Quitar esta zona"
            };
            del.Click += (_, e) => { Remove((int)g.Tag); e.Handled = true; };
            g.Children.Add(del);

            for (int corner = 0; corner < 4; corner++) g.Children.Add(Grip(corner));

            g.MouseLeftButtonDown += Zone_MouseLeftButtonDown;
            g.MouseEnter += (s, _) => Highlight((Grid)s, true);
            g.MouseLeave += (s, _) => Highlight((Grid)s, false);
            return g;
        }

        private static void Highlight(Grid g, bool on)
        {
            if (g.Children.Count > 0 && g.Children[0] is Border b) b.Background = on ? ZoneFillHot : ZoneFill;
        }

        /// <summary>0 = arriba-izquierda, 1 = arriba-derecha, 2 = abajo-izquierda, 3 = abajo-derecha.</summary>
        private static Rectangle Grip(int corner)
        {
            return new Rectangle
            {
                Width = 18,
                Height = 18,
                RadiusX = 5,
                RadiusY = 5,
                Fill = GripFill,
                Margin = new Thickness(6),
                Tag = "grip" + corner,
                HorizontalAlignment = corner is 0 or 2 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = corner is 0 or 1 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Cursor = corner is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW
            };
        }

        private void Place(int i)
        {
            var r = _zones[i];
            var g = _visuals[i];
            Canvas.SetLeft(g, r.X);
            Canvas.SetTop(g, r.Y);
            g.Width = r.Width;
            g.Height = r.Height;
        }

        private void Remove(int index)
        {
            if (index < 0 || index >= _zones.Count || _zones.Count == 1) return;
            _zones.RemoveAt(index);
            Render();
        }

        // ---------------- Arrastre ----------------

        private int _dragIndex = -1;
        private int _dragCorner = -1;      // -1 = mover
        private Point _dragFrom;
        private Rect _dragOrigin;

        private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var g = (Grid)sender;
            _dragIndex = (int)g.Tag;
            _dragCorner = e.OriginalSource is Rectangle rect && rect.Tag is string t && t.StartsWith("grip")
                ? t[4] - '0'
                : -1;
            _dragFrom = e.GetPosition(Board);
            _dragOrigin = _zones[_dragIndex];

            // La zona que se toca pasa al frente
            Board.Children.Remove(g);
            Board.Children.Add(g);

            g.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(Board);
            double dx = p.X - _dragFrom.X, dy = p.Y - _dragFrom.Y;
            double w = Board.ActualWidth, h = Board.ActualHeight;
            var o = _dragOrigin;
            Rect r;

            if (_dragCorner < 0)
            {
                double x = Math.Clamp(o.X + dx, 0, Math.Max(0, w - o.Width));
                double y = Math.Clamp(o.Y + dy, 0, Math.Max(0, h - o.Height));
                (x, y) = (SnapEdge(x, o.Width, w, true), SnapEdge(y, o.Height, h, false));
                r = new Rect(x, y, o.Width, o.Height);
            }
            else
            {
                double left = o.X, top = o.Y, right = o.Right, bottom = o.Bottom;
                if (_dragCorner is 0 or 2) left = Math.Min(o.Right - MinW, o.X + dx); else right = Math.Max(o.X + MinW, o.Right + dx);
                if (_dragCorner is 0 or 1) top = Math.Min(o.Bottom - MinH, o.Y + dy); else bottom = Math.Max(o.Y + MinH, o.Bottom + dy);

                left = Math.Clamp(SnapValue(left, GuidesX(), 0), 0, w);
                right = Math.Clamp(SnapValue(right, GuidesX(), w), 0, w);
                top = Math.Clamp(SnapValue(top, GuidesY(), 0), 0, h);
                bottom = Math.Clamp(SnapValue(bottom, GuidesY(), h), 0, h);

                r = new Rect(left, top, Math.Max(MinW, right - left), Math.Max(MinH, bottom - top));
            }

            _zones[_dragIndex] = r;
            Place(_dragIndex);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragIndex < 0) return;
            _visuals[_dragIndex].ReleaseMouseCapture();
            _dragIndex = -1;
        }

        // Imán: bordes del monitor, centro y bordes de las demás zonas

        private IEnumerable<double> GuidesX()
        {
            double w = Board.ActualWidth;
            yield return 0;
            yield return w;
            yield return w / 2;
            for (int i = 0; i < _zones.Count; i++)
            {
                if (i == _dragIndex) continue;
                yield return _zones[i].X;
                yield return _zones[i].Right;
            }
        }

        private IEnumerable<double> GuidesY()
        {
            double h = Board.ActualHeight;
            yield return 0;
            yield return h;
            yield return h / 2;
            for (int i = 0; i < _zones.Count; i++)
            {
                if (i == _dragIndex) continue;
                yield return _zones[i].Y;
                yield return _zones[i].Bottom;
            }
        }

        private static double SnapValue(double v, IEnumerable<double> guides, double fallback)
        {
            double best = v, dist = SnapDistance;
            foreach (var g in guides)
            {
                double d = Math.Abs(v - g);
                if (d < dist) { dist = d; best = g; }
            }
            return best;
        }

        /// <summary>Al mover, el imán actúa sobre los dos bordes de la zona.</summary>
        private double SnapEdge(double pos, double size, double total, bool horizontal)
        {
            var guides = (horizontal ? GuidesX() : GuidesY()).ToList();
            double start = SnapValue(pos, guides, pos);
            if (Math.Abs(start - pos) <= SnapDistance) pos = start;
            double end = SnapValue(pos + size, guides, pos + size);
            if (Math.Abs(end - (pos + size)) <= SnapDistance) pos = end - size;
            return Math.Clamp(pos, 0, Math.Max(0, total - size));
        }

        // ---------------- Botones ----------------

        private void Board_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) AddZone(e.GetPosition(Board));
        }

        private void Add_Click(object sender, RoutedEventArgs e) => AddZone(null);

        private void AddZone(Point? at)
        {
            double w = Board.ActualWidth, h = Board.ActualHeight;
            double zw = Math.Max(MinW, w * 0.35), zh = Math.Max(MinH, h * 0.35);
            double x = at?.X - zw / 2 ?? w / 2 - zw / 2 + _zones.Count % 5 * 26;
            double y = at?.Y - zh / 2 ?? h / 2 - zh / 2 + _zones.Count % 5 * 26;
            _zones.Add(new Rect(Math.Clamp(x, 0, Math.Max(0, w - zw)), Math.Clamp(y, 0, Math.Max(0, h - zh)), zw, zh));
            Render();
        }

        /// <summary>Rellena el lienzo con una cuadrícula pareja (punto de partida cómodo).</summary>
        private void FromGrid_Click(object sender, RoutedEventArgs e)
        {
            int cols = Math.Clamp(_zones.Count <= 1 ? 2 : (int)Math.Ceiling(Math.Sqrt(_zones.Count)), 2, 6);
            int rows = Math.Clamp((int)Math.Ceiling(_zones.Count / (double)cols), 1, 4);
            double w = Board.ActualWidth, h = Board.ActualHeight;
            _zones.Clear();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    _zones.Add(new Rect(c * w / cols, r * h / rows, w / cols, h / rows));
            Render();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            double w = Board.ActualWidth, h = Board.ActualHeight;
            if (w < 80 || h < 80 || _zones.Count == 0) { Close(); return; }

            var layout = new CustomZoneLayout
            {
                Id = _id,
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Mi diseño" : NameBox.Text.Trim(),
                Zones = _zones.Select(z => new ZoneBox
                {
                    X = Math.Round(Math.Clamp(z.X / w, 0, 1), 4),
                    Y = Math.Round(Math.Clamp(z.Y / h, 0, 1), 4),
                    W = Math.Round(Math.Clamp(z.Width / w, 0.02, 1), 4),
                    H = Math.Round(Math.Clamp(z.Height / h, 0.02, 1), 4)
                }).ToList()
            };
            Saved?.Invoke(layout);
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && !NameBox.IsFocused) { Save_Click(sender, e); e.Handled = true; }
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private const uint SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040;
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
