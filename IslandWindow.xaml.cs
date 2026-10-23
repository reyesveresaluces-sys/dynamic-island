using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicIsland
{
    public partial class IslandWindow : Window
    {
        private enum IslandState { Collapsed, Banner, History }

        // Dimensiones de cada estado
        private const double CollapsedW = 150, CollapsedH = 34;
        private const double BannerW = 420, BannerH = 82;
        private const double HistoryW = 430, HistoryRowH = 66, HistoryHeaderH = 58, HistoryMaxRows = 5;
        private const int MaxHistory = 40;

        public double BannerSeconds { get; set; } = 5;
        public bool Paused { get; set; }

        private readonly ObservableCollection<IslandNotification> _history = new();
        private readonly Queue<IslandNotification> _pending = new();
        private readonly DispatcherTimer _bannerTimer = new();
        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        private IslandState _state = IslandState.Collapsed;
        private IslandNotification? _current;
        private int _unread;

        private MediaService? _media;
        private readonly List<ScaleTransform> _eqScales = new();
        private bool _eqRunning;
        public bool ShowTrackChanges { get; set; } = true;
        private double CollapsedWidth => _media?.HasSession == true ? 196 : CollapsedW;

        /// <summary>Se dispara cuando el usuario descarta una notificación (para quitarla del Centro de actividades).</summary>
        public event Action<IslandNotification>? Dismissed;
        public event Action? ClearedAll;
        public event Action? HistoryOpened;

        /// <summary>Nombre del monitor (Screen.DeviceName) donde vive esta isla.</summary>
        public string ScreenId { get; }
        public IReadOnlyList<IslandNotification> History => _history;

        public IslandWindow(string screenId)
        {
            ScreenId = screenId;
            InitializeComponent();
            HistoryList.ItemsSource = _history;

            _bannerTimer.Tick += (_, _) =>
            {
                if (Pill.IsMouseOver) return; // mientras el mouse esté encima, no se cierra
                _bannerTimer.Stop();
                ShowNext();
            };
            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (Pill.IsMouseOver && _state == IslandState.Collapsed) GoHistory();
            };
            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();
            UpdateClock();
            BuildEqualizer();

            SourceInitialized += (_, _) => { MakeToolWindow(); PositionWindow(); };
            Loaded += (_, _) => PositionWindow();
            // Al pasar a un monitor con otra escala, WPF cambia el tamaño: volvemos a centrar
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PositionWindow), DispatcherPriority.Background);
            Closed += (_, _) =>
            {
                if (_media != null)
                {
                    _media.Changed -= UpdateMedia;
                    _media.TrackChanged -= OnTrackChanged;
                }
                _clockTimer.Stop();
                _bannerTimer.Stop();
                SetEqualizer(false);
            };
        }

        // ---------------- API pública ----------------

        /// <summary>Copia el historial de otra isla (cuando aparece un monitor nuevo).</summary>
        public void LoadHistory(IEnumerable<IslandNotification> items)
        {
            foreach (var n in items) _history.Add(n);
        }

        /// <summary>Marca como vistas (otra isla abrió el historial).</summary>
        public void MarkSeen()
        {
            _unread = 0;
            UpdateBadge();
        }

        /// <summary>Quita una notificación descartada en otra isla.</summary>
        public void RemoveItem(IslandNotification n)
        {
            _history.Remove(n);
            if (_state == IslandState.History) ResizeHistory();
        }

        public void ClearHistory()
        {
            _history.Clear();
            if (_state == IslandState.History) ResizeHistory();
        }

        public void Push(IslandNotification n)
        {
            _history.Insert(0, n);
            while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);

            if (_state == IslandState.History)
            {
                ResizeHistory();
                return;
            }

            _unread++;
            UpdateBadge();
            if (Paused) return;

            _pending.Enqueue(n);
            if (_state == IslandState.Collapsed) ShowNext();
        }

        // ---------------- Música ----------------

        public void AttachMedia(MediaService media)
        {
            _media = media;
            media.Changed += UpdateMedia;
            media.TrackChanged += OnTrackChanged;
            UpdateMedia(); // por si ya está sonando algo
        }

        private void UpdateMedia()
        {
            if (_media == null) return;
            bool has = _media.HasSession;

            MiniArt.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            MiniArtBrush.ImageSource = _media.Art;
            CollapsedView.Margin = has ? new Thickness(7, 0, 14, 0) : new Thickness(16, 0, 16, 0);

            MusicCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            BigArtBrush.ImageSource = _media.Art;
            MusicTitle.Text = _media.Title;
            MusicArtist.Text = _media.Artist;
            MusicApp.Text = _media.AppName;
            PlayPauseGlyph.Text = _media.IsPlaying ? "" : "";

            UpdateBadge();
            if (_state == IslandState.Collapsed) Animate(CollapsedWidth, CollapsedH, 17);
            else if (_state == IslandState.History) ResizeHistory();
        }

        private void OnTrackChanged()
        {
            if (_media == null || !ShowTrackChanges || Paused || _state != IslandState.Collapsed) return;
            _pending.Enqueue(new IslandNotification
            {
                AppName = "Reproduciendo · " + _media.AppName,
                AppId = _media.AppId,
                Title = _media.Title,
                Body = _media.Artist,
                Icon = _media.Art
            });
            ShowNext();
        }

        private void MusicPrev_Click(object sender, RoutedEventArgs e) => _media?.Previous();
        private void MusicNext_Click(object sender, RoutedEventArgs e) => _media?.Next();
        private void MusicPlayPause_Click(object sender, RoutedEventArgs e) => _media?.TogglePlayPause();

        private void BuildEqualizer()
        {
            for (int i = 0; i < 4; i++)
            {
                var scale = new ScaleTransform(1, 0.35);
                _eqScales.Add(scale);
                EqBars.Children.Add(new Rectangle
                {
                    Width = 3,
                    Height = 14,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = scale
                });
            }
        }

        private void SetEqualizer(bool run)
        {
            if (run == _eqRunning) return;
            _eqRunning = run;
            double[] speeds = { 420, 310, 520, 370 };
            for (int i = 0; i < _eqScales.Count; i++)
            {
                if (run)
                {
                    var a = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(speeds[i]))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    _eqScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else
                {
                    _eqScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
            }
        }

        // ---------------- Estados ----------------

        private void ShowNext()
        {
            if (_pending.Count == 0)
            {
                GoCollapsed();
                return;
            }

            _current = _pending.Dequeue();
            BannerAvatar.Content = _current;
            BannerApp.Text = _current.AppName;
            BannerTime.Text = "ahora";
            BannerTitle.Text = _current.Headline;
            BannerBody.Text = _current.Body;
            BannerBody.Visibility = _current.BodyVisibility;

            bool wasBanner = _state == IslandState.Banner;
            _state = IslandState.Banner;
            Animate(BannerW, BannerH, 28);
            ShowView(BannerView, restart: wasBanner);

            _bannerTimer.Interval = TimeSpan.FromSeconds(BannerSeconds);
            _bannerTimer.Start();
        }

        private void GoCollapsed()
        {
            _state = IslandState.Collapsed;
            _current = null;
            _bannerTimer.Stop();
            Animate(CollapsedWidth, CollapsedH, 17);
            ShowView(CollapsedView);
        }

        private void GoHistory()
        {
            _state = IslandState.History;
            _bannerTimer.Stop();
            _pending.Clear();
            _unread = 0;
            UpdateBadge();
            ResizeHistory();
            ShowView(HistoryView);
            HistoryOpened?.Invoke();
        }

        private void ResizeHistory()
        {
            int rows = Math.Min(_history.Count, (int)HistoryMaxRows);
            EmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            double music = MusicCard.Visibility == Visibility.Visible ? 86 : 0;
            double h = HistoryHeaderH + music + (rows == 0 ? 60 : rows * HistoryRowH) + 14;
            Animate(HistoryW, h, 30);
        }

        // ---------------- Animación ----------------

        private void Animate(double w, double h, double radius)
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
            var dur = TimeSpan.FromMilliseconds(460);
            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
            Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
            Pill.CornerRadius = new CornerRadius(radius);
        }

        private void ShowView(FrameworkElement target, bool restart = false)
        {
            foreach (var v in new FrameworkElement[] { CollapsedView, BannerView, HistoryView })
            {
                if (v == target)
                {
                    v.Visibility = Visibility.Visible;
                    var fadeIn = new DoubleAnimation(restart ? 0 : v.Opacity, 1, TimeSpan.FromMilliseconds(260))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(restart ? 60 : 140)
                    };
                    v.BeginAnimation(OpacityProperty, fadeIn);
                }
                else if (v.Visibility == Visibility.Visible)
                {
                    var view = v;
                    var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
                    fadeOut.Completed += (_, _) =>
                    {
                        if (CurrentView() != view) view.Visibility = Visibility.Collapsed;
                    };
                    view.BeginAnimation(OpacityProperty, fadeOut);
                }
            }
        }

        private FrameworkElement CurrentView() => _state switch
        {
            IslandState.Banner => BannerView,
            IslandState.History => HistoryView,
            _ => CollapsedView
        };

        // ---------------- Eventos de mouse ----------------

        private void Pill_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_state == IslandState.Collapsed) _hoverTimer.Start();
        }

        private void Pill_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (_state == IslandState.History)
            {
                ShowNext(); // colapsa (o muestra lo pendiente)
            }
            else if (_state == IslandState.Banner)
            {
                _bannerTimer.Interval = TimeSpan.FromSeconds(1.5);
                _bannerTimer.Start();
            }
        }

        private void Banner_Click(object sender, MouseButtonEventArgs e)
        {
            if (_current != null) Open(_current);
            _bannerTimer.Stop();
            ShowNext();
        }

        private void Banner_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_current != null) Dismiss(_current);
            _bannerTimer.Stop();
            ShowNext();
        }

        private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            if ((sender as FrameworkElement)?.Tag is IslandNotification n) Open(n);
        }

        private void HistoryItemRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is IslandNotification n) Dismiss(n);
            e.Handled = true;
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            ClearedAll?.Invoke();
            ResizeHistory();
        }

        private void Dismiss(IslandNotification n)
        {
            _history.Remove(n);
            Dismissed?.Invoke(n);
            if (_state == IslandState.History) ResizeHistory();
        }

        private static void Open(IslandNotification n)
        {
            try
            {
                if (n.OnClick != null) { n.OnClick(); return; }
                if (string.IsNullOrWhiteSpace(n.AppId)) return;

                // Igual que el Centro de notificaciones: la app recibe la orden original de la notificación
                NotificationActivation.Open(n);
            }
            catch { /* la app no se pudo abrir */ }
        }

        // ---------------- Utilidades ----------------

        private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm");

        private void UpdateBadge()
        {
            bool unread = _unread > 0;
            bool playing = !unread && _media?.HasSession == true && _media.IsPlaying;
            UnreadBadge.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
            EqBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            IdleDot.Visibility = !unread && !playing ? Visibility.Visible : Visibility.Collapsed;
            UnreadText.Text = _unread > 99 ? "99+" : _unread.ToString();
            SetEqualizer(playing);
        }

        /// <summary>Centra la isla arriba del monitor que le corresponde (en píxeles físicos).</summary>
        public void PositionWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            System.Windows.Forms.Screen? screen = null;
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
                if (s.DeviceName == ScreenId) { screen = s; break; }
            screen ??= System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            var area = screen.WorkingArea;
            if (!GetWindowRect(hwnd, out var r)) return;
            int width = r.Right - r.Left;
            int x = area.Left + (area.Width - width) / 2;
            int y = area.Top;
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>Vuelve a colocar la isla por encima de todas las ventanas (sin robar el foco).</summary>
        public void BringToTop()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!IsVisible) Show();
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var cur = child;
            while (cur != null)
            {
                if (cur is T t) return t;
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        // Oculta la ventana de Alt+Tab y evita que robe el foco
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private void MakeToolWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        }
    }
}
