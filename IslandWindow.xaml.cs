using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private enum IslandState { Collapsed, Banner, Expanded }

        // Dimensiones
        private const double CollapsedW = 150, CollapsedH = 34;
        private const double SideW = 30, SideH = 118;
        private const double BannerW = 420, BannerH = 82;
        private const double ExpandedW = 450, HeaderH = 50, Pad = 24, RowH = 66;
        private const int MaxHistory = 40;

        public double BannerSeconds { get; set; } = 5;
        public bool Paused { get; set; }
        public bool ShowTrackChanges { get; set; } = true;

        private readonly ObservableCollection<IslandNotification> _history = new();
        private readonly Queue<IslandNotification> _pending = new();
        private readonly DispatcherTimer _bannerTimer = new();
        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(6) };
        private IslandState _state = IslandState.Collapsed;
        private IslandSpace _space = IslandSpace.Notifications;
        private IslandDock _dock = IslandDock.Top;
        private IslandNotification? _current;
        private int _unread;

        // Abierta con el atajo, agitando el mouse o arrastrando algo encima
        private bool _pinned, _keyboard, _mouseWasInside, _dragOpened;

        private MediaService? _media;
        private readonly List<ScaleTransform> _eqScales = new();
        private bool _eqRunning;
        private double CollapsedWidth => _media?.HasSession == true ? 196 : CollapsedW;

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

            if (Enum.TryParse(AppSettings.Current.LastSpace, out IslandSpace last)) _space = last;

            _bannerTimer.Tick += (_, _) =>
            {
                if (Pill.IsMouseOver) return; // mientras el mouse esté encima, no se cierra
                _bannerTimer.Stop();
                ShowNext();
            };
            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (Pill.IsMouseOver && _state == IslandState.Collapsed) GoExpanded(DefaultSpace());
            };
            _autoClose.Tick += (_, _) =>
            {
                _autoClose.Stop();
                if (_state == IslandState.Expanded && _pinned && !_keyboard && !_mouseWasInside && !Pill.IsMouseOver) Close_();
            };
            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();
            IslandTools.Timers.PropertyChanged += OnTimersChanged;
            IslandTools.Launcher.Items.CollectionChanged += (_, _) => { if (_state == IslandState.Expanded) Resize(); };
            ClipboardSpaceView.KeyboardRequested += () => ActivateForInput();
            UpdateClock();
            BuildEqualizer();

            Deactivated += (_, _) =>
            {
                // Abierta para escribir: se cierra al hacer clic fuera
                if (_keyboard && _state == IslandState.Expanded) Close_();
            };
            PreviewKeyDown += OnKeyDown;

            SourceInitialized += (_, _) =>
            {
                MakeToolWindow();
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
                ApplyDock(AppSettings.Current.Dock, animate: false);
            };
            Loaded += (_, _) => PositionWindow();
            // Al pasar a un monitor con otra escala, WPF cambia el tamaño: volvemos a colocar
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PositionWindow), DispatcherPriority.Background);
            Closed += (_, _) =>
            {
                if (_media != null)
                {
                    _media.Changed -= UpdateMedia;
                    _media.TrackChanged -= OnTrackChanged;
                }
                IslandTools.Timers.PropertyChanged -= OnTimersChanged;
                MediaSpaceView.SetActive(false);
                _clockTimer.Stop();
                _bannerTimer.Stop();
                SetEqualizer(false);
            };
        }

        // ================= API pública =================

        public void LoadHistory(IEnumerable<IslandNotification> items)
        {
            foreach (var n in items) _history.Add(n);
        }

        public void MarkSeen()
        {
            _unread = 0;
            UpdateBadge();
        }

        public void RemoveItem(IslandNotification n)
        {
            _history.Remove(n);
            if (_state == IslandState.Expanded) Resize();
        }

        public void ClearHistory()
        {
            _history.Clear();
            if (_state == IslandState.Expanded) Resize();
        }

        public void Push(IslandNotification n)
        {
            _history.Insert(0, n);
            while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);

            if (_state == IslandState.Expanded)
            {
                if (_space == IslandSpace.Notifications) Resize();
                else { _unread++; UpdateBadge(); }
                return;
            }

            _unread++;
            UpdateBadge();
            if (Paused) return;

            _pending.Enqueue(n);
            if (_state == IslandState.Collapsed) ShowNext();
        }

        /// <summary>Abre la isla sin necesidad de pasar el mouse (atajo de teclado o Magic Shake).</summary>
        public void OpenPinned(bool keyboard)
        {
            _pinned = true;
            _mouseWasInside = Pill.IsMouseOver;
            if (_state != IslandState.Expanded) GoExpanded(DefaultSpace());
            if (keyboard) ActivateForInput();
            else { _autoClose.Stop(); _autoClose.Start(); }
        }

        public bool IsExpanded => _state == IslandState.Expanded;

        public void ToggleFromKeyboard()
        {
            if (_state == IslandState.Expanded && _keyboard) Close_();
            else OpenPinned(keyboard: true);
        }

        /// <summary>Cambia el borde donde se ancla la isla: arriba, izquierda o derecha.</summary>
        public void ApplyDock(string dock, bool animate = true)
        {
            Enum.TryParse(dock, out IslandDock d);
            _dock = d;
            switch (_dock)
            {
                case IslandDock.Left:
                    Pill.HorizontalAlignment = HorizontalAlignment.Left;
                    Pill.VerticalAlignment = VerticalAlignment.Center;
                    Pill.Margin = new Thickness(6, 0, 0, 0);
                    break;
                case IslandDock.Right:
                    Pill.HorizontalAlignment = HorizontalAlignment.Right;
                    Pill.VerticalAlignment = VerticalAlignment.Center;
                    Pill.Margin = new Thickness(0, 0, 6, 0);
                    break;
                default:
                    Pill.HorizontalAlignment = HorizontalAlignment.Center;
                    Pill.VerticalAlignment = VerticalAlignment.Top;
                    Pill.Margin = new Thickness(0, 8, 0, 0);
                    break;
            }
            if (_state == IslandState.Collapsed)
            {
                if (animate) GoCollapsed();
                else
                {
                    var (w, h, r) = CollapsedSize();
                    Pill.BeginAnimation(WidthProperty, null);
                    Pill.BeginAnimation(HeightProperty, null);
                    Pill.Width = w; Pill.Height = h; Pill.CornerRadius = new CornerRadius(r);
                    ShowView(CollapsedViewForDock());
                }
            }
            PositionWindow();
        }

        // ================= Música =================

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
            SideArt.Visibility = has && _media.Art != null ? Visibility.Visible : Visibility.Collapsed;
            SideArtBrush.ImageSource = _media.Art;
            CollapsedView.Margin = has ? new Thickness(7, 0, 14, 0) : new Thickness(16, 0, 16, 0);

            UpdateBadge();
            if (_state == IslandState.Collapsed) AnimateTo(CollapsedSize());
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

        private void BuildEqualizer()
        {
            for (int i = 0; i < 4; i++)
            {
                var scale = new ScaleTransform(1, 0.35);
                _eqScales.Add(scale);
                foreach (var host in new[] { EqBars, SideEqBars })
                {
                    host.Children.Add(new Rectangle
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

        // ================= Estados =================

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
            LeaveExpanded();
            _state = IslandState.Banner;
            AnimateTo((BannerW, BannerH, 28));
            ShowView(BannerView, restart: wasBanner);

            _bannerTimer.Interval = TimeSpan.FromSeconds(BannerSeconds);
            _bannerTimer.Start();
        }

        private void GoCollapsed()
        {
            LeaveExpanded();
            _state = IslandState.Collapsed;
            _current = null;
            _bannerTimer.Stop();
            AnimateTo(CollapsedSize());
            ShowView(CollapsedViewForDock());
        }

        private void GoExpanded(IslandSpace space)
        {
            bool wasExpanded = _state == IslandState.Expanded;
            _state = IslandState.Expanded;
            _bannerTimer.Stop();
            _pending.Clear();
            SelectSpace(space, animate: wasExpanded);
            if (!wasExpanded) ShowView(ExpandedView);
        }

        /// <summary>Cierra la isla expandida (vuelve a compacta o muestra lo pendiente).</summary>
        private void Close_()
        {
            _pinned = _dragOpened = false;
            _autoClose.Stop();
            ShowNext();
        }

        private void LeaveExpanded()
        {
            if (_state != IslandState.Expanded) return;
            MediaSpaceView.SetActive(false);
            ClipboardSpaceView.SetActive(false);
            LauncherSpaceView.SetDropHint(false);
            _pinned = _dragOpened = false;
            if (_keyboard) RestoreNoActivate();
        }

        private IslandSpace DefaultSpace()
        {
            if (_unread > 0) return IslandSpace.Notifications;
            return _space;
        }

        // ================= Espacios =================

        private static readonly IslandSpace[] Order =
            { IslandSpace.Notifications, IslandSpace.Media, IslandSpace.Clipboard, IslandSpace.Launcher, IslandSpace.Timer };

        private bool _selecting;

        private void SelectSpace(IslandSpace space, bool animate, int direction = 0)
        {
            var old = _space;
            _space = space;
            AppSettings.Current.LastSpace = space.ToString();

            _selecting = true;
            TabFor(space).IsChecked = true;
            _selecting = false;

            NotificationsSpace.Visibility = space == IslandSpace.Notifications ? Visibility.Visible : Visibility.Collapsed;
            MediaSpaceView.Visibility = space == IslandSpace.Media ? Visibility.Visible : Visibility.Collapsed;
            ClipboardSpaceView.Visibility = space == IslandSpace.Clipboard ? Visibility.Visible : Visibility.Collapsed;
            LauncherSpaceView.Visibility = space == IslandSpace.Launcher ? Visibility.Visible : Visibility.Collapsed;
            TimerSpaceView.Visibility = space == IslandSpace.Timer ? Visibility.Visible : Visibility.Collapsed;

            MediaSpaceView.SetActive(space == IslandSpace.Media);
            ClipboardSpaceView.SetActive(space == IslandSpace.Clipboard);

            ClearButton.Visibility = space == IslandSpace.Notifications && _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SpaceTitle.Text = space switch
            {
                IslandSpace.Media => "Música",
                IslandSpace.Clipboard => "Portapapeles",
                IslandSpace.Launcher => "Apps",
                IslandSpace.Timer => "Temporizador",
                _ => "Notificaciones"
            };

            if (space == IslandSpace.Notifications)
            {
                _unread = 0;
                UpdateBadge();
                HistoryOpened?.Invoke();
            }

            Resize();

            // Transición: el contenido entra deslizándose desde el lado del cambio
            if (animate && old != space)
            {
                if (direction == 0) direction = Array.IndexOf(Order, space) > Array.IndexOf(Order, old) ? 1 : -1;
                var dur = TimeSpan.FromMilliseconds(380);
                SpaceShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(36 * direction, 0, dur) { EasingFunction = new SpringEase() });
                SpaceHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            }
        }

        private RadioButton TabFor(IslandSpace s) => s switch
        {
            IslandSpace.Media => TabMedia,
            IslandSpace.Clipboard => TabClipboard,
            IslandSpace.Launcher => TabLauncher,
            IslandSpace.Timer => TabTimer,
            _ => TabNotifications
        };

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (_selecting || _state != IslandState.Expanded) return;
            var s = sender == TabMedia ? IslandSpace.Media
                  : sender == TabClipboard ? IslandSpace.Clipboard
                  : sender == TabLauncher ? IslandSpace.Launcher
                  : sender == TabTimer ? IslandSpace.Timer
                  : IslandSpace.Notifications;
            if (s != _space) SelectSpace(s, animate: true);
        }

        private void Step(int direction)
        {
            int i = Array.IndexOf(Order, _space);
            int next = (i + direction + Order.Length) % Order.Length;
            SelectSpace(Order[next], animate: true, direction: direction);
        }

        /// <summary>Tamaño de la isla expandida según el espacio.</summary>
        private void Resize()
        {
            if (_state != IslandState.Expanded) return;
            double content = _space switch
            {
                IslandSpace.Media => 222,
                IslandSpace.Clipboard => 390,
                IslandSpace.Launcher => LauncherHeight(),
                IslandSpace.Timer => 262,
                _ => _history.Count == 0 ? 70 : Math.Min(_history.Count, 5) * RowH
            };
            if (_space == IslandSpace.Notifications)
            {
                EmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ClearButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            AnimateTo((ExpandedW, HeaderH + content + Pad, 30));
        }

        private double LauncherHeight()
        {
            int n = LauncherSpaceView.Count;
            if (n == 0) return 150;
            int rows = Math.Clamp((int)Math.Ceiling(n / 5.0), 1, 3);
            return rows * 84 + 44;
        }

        // ================= Animación (resorte) =================

        private (double w, double h, double r) CollapsedSize() =>
            _dock == IslandDock.Top ? (CollapsedWidth, CollapsedH, 17) : (SideW, SideH, 15);

        private FrameworkElement CollapsedViewForDock() => _dock == IslandDock.Top ? CollapsedView : CollapsedSideView;

        private void AnimateTo((double w, double h, double r) size)
        {
            var dur = TimeSpan.FromMilliseconds(560);
            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(size.w, dur) { EasingFunction = new SpringEase() });
            Pill.BeginAnimation(HeightProperty, new DoubleAnimation(size.h, dur) { EasingFunction = new SpringEase() });
            Pill.CornerRadius = new CornerRadius(size.r);
        }

        private void ShowView(FrameworkElement target, bool restart = false)
        {
            foreach (var v in new FrameworkElement[] { CollapsedView, CollapsedSideView, BannerView, ExpandedView })
            {
                if (v == target)
                {
                    v.Visibility = Visibility.Visible;
                    var fadeIn = new DoubleAnimation(restart ? 0 : v.Opacity, 1, TimeSpan.FromMilliseconds(240))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(restart ? 60 : 130)
                    };
                    v.BeginAnimation(OpacityProperty, fadeIn);
                }
                else if (v.Visibility == Visibility.Visible)
                {
                    var view = v;
                    var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
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
            IslandState.Expanded => ExpandedView,
            _ => CollapsedViewForDock()
        };

        // ================= Mouse, rueda y teclado =================

        private void Pill_MouseEnter(object sender, MouseEventArgs e)
        {
            _mouseWasInside = true;
            if (_state == IslandState.Collapsed) _hoverTimer.Start();
        }

        private void Pill_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (_state == IslandState.Expanded)
            {
                if (_keyboard) return;            // se cierra con Esc o clic fuera
                if (Mouse.LeftButton == MouseButtonState.Pressed) return;
                if (_menuOpen) return;            // menú contextual del lanzador abierto
                Close_();
            }
            else if (_state == IslandState.Banner)
            {
                _bannerTimer.Interval = TimeSpan.FromSeconds(1.5);
                _bannerTimer.Start();
            }
        }

        private bool _menuOpen;

        private void Pill_ContextMenuOpening(object sender, ContextMenuEventArgs e) => _menuOpen = true;

        private void Pill_ContextMenuClosing(object sender, ContextMenuEventArgs e)
        {
            _menuOpen = false;
            // Si el mouse ya no está sobre la isla al cerrar el menú, cerrarla
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_state == IslandState.Expanded && !_keyboard && !IsCursorOverPill()) Close_();
            }), DispatcherPriority.Background);
        }

        private double _wheelAcc;
        private DateTime _lastStep;

        private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_state != IslandState.Expanded) return;

            // Dentro de una lista que se puede desplazar: desplazar, no cambiar de espacio
            if (e.OriginalSource is DependencyObject d)
            {
                if (FindParent<Slider>(d) != null) return;
                var sv = FindParent<ScrollViewer>(d);
                if (sv != null && sv.ScrollableHeight > 0.5) return;
            }
            WheelStep(e.Delta);
            e.Handled = true;
        }

        private void WheelStep(int delta)
        {
            _wheelAcc += delta;
            if (Math.Abs(_wheelAcc) < 100) return;               // touchpads mandan pasos pequeños
            if ((DateTime.Now - _lastStep).TotalMilliseconds < 260) { _wheelAcc = 0; return; }
            Step(_wheelAcc < 0 ? 1 : -1);
            _wheelAcc = 0;
            _lastStep = DateTime.Now;
        }

        private const int WM_MOUSEHWHEEL = 0x020E;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Desplazamiento horizontal del touchpad: cambia de espacio en cualquier parte
            if (msg == WM_MOUSEHWHEEL && _state == IslandState.Expanded)
            {
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                WheelStep(-delta);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_state != IslandState.Expanded) return;
            bool inText = Keyboard.FocusedElement is TextBox;
            if (e.Key == Key.Escape) { Close_(); e.Handled = true; }
            else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
            else if (!inText && (e.Key == Key.Right || e.Key == Key.Left))
            {
                Step(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
            }
            else if (!inText && e.Key >= Key.D1 && e.Key <= Key.D5)
            {
                SelectSpace(Order[e.Key - Key.D1], animate: true);
                e.Handled = true;
            }
        }

        // ================= Arrastrar y soltar (al lanzador) =================

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void Pill_DragEnter(object sender, DragEventArgs e)
        {
            if (!HasFiles(e)) return;
            if (_state != IslandState.Expanded || _space != IslandSpace.Launcher)
            {
                _dragOpened = true;
                GoExpanded(IslandSpace.Launcher);
                if (_state == IslandState.Expanded && _space != IslandSpace.Launcher) SelectSpace(IslandSpace.Launcher, true);
            }
            LauncherSpaceView.SetDropHint(true);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Pill_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Pill_DragLeave(object sender, DragEventArgs e)
        {
            LauncherSpaceView.SetDropHint(false);
            if (!_dragOpened) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_dragOpened && !IsCursorOverPill()) Close_();
            };
            t.Start();
        }

        private void Pill_Drop(object sender, DragEventArgs e)
        {
            LauncherSpaceView.SetDropHint(false);
            _dragOpened = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) IslandTools.Launcher.Add(f);
            e.Handled = true;
        }

        private bool IsCursorOverPill()
        {
            if (!GetCursorPos(out var p)) return false;
            var topLeft = Pill.PointToScreen(new Point(0, 0));
            var bottomRight = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
            return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
        }

        // ================= Notificaciones: clics =================

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
            Resize();
        }

        private void Dismiss(IslandNotification n)
        {
            _history.Remove(n);
            Dismissed?.Invoke(n);
            if (_state == IslandState.Expanded) Resize();
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

        // ================= Isla compacta: reloj, temporizador, indicadores =================

        private void OnTimersChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TimerTools.CompactText)) UpdateClock();
        }

        private static readonly Brush TimerBrush = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
        private static readonly Brush StopwatchBrush = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private void UpdateClock()
        {
            var t = IslandTools.Timers;
            string? compact = t.CompactText;
            if (compact != null)
            {
                ClockText.Text = compact;
                ClockText.Foreground = t.CompactIsTimer ? TimerBrush : StopwatchBrush;
                SideTimerText.Text = compact;
                SideTimerText.Foreground = ClockText.Foreground;
                SideTimerText.Visibility = Visibility.Visible;
            }
            else
            {
                ClockText.Text = DateTime.Now.ToString("HH:mm");
                ClockText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                SideTimerText.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateBadge()
        {
            bool unread = _unread > 0;
            bool playing = !unread && _media?.HasSession == true && _media.IsPlaying;
            UnreadBadge.Visibility = SideBadge.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
            EqBars.Visibility = SideEqBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            IdleDot.Visibility = SideDot.Visibility = !unread && !playing ? Visibility.Visible : Visibility.Collapsed;
            UnreadText.Text = SideBadgeText.Text = _unread > 99 ? "99+" : _unread.ToString();
            SetEqualizer(playing);
        }

        // ================= Ventana: posición, foco, siempre visible =================

        /// <summary>Coloca la ventana en el borde elegido del monitor que le corresponde (píxeles físicos).</summary>
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
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            int x, y;
            switch (_dock)
            {
                case IslandDock.Left:
                    x = area.Left; y = area.Top + (area.Height - h) / 2;
                    break;
                case IslandDock.Right:
                    x = area.Right - w; y = area.Top + (area.Height - h) / 2;
                    break;
                default:
                    x = area.Left + (area.Width - w) / 2; y = area.Top;
                    break;
            }
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

        /// <summary>Permite escribir en la isla (búsqueda del portapapeles, atajos de teclado).</summary>
        private void ActivateForInput()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            _keyboard = true;
            _pinned = true;
            _autoClose.Stop();
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_NOACTIVATE));
            Activate();
            SetForegroundWindow(hwnd);
        }

        private void RestoreNoActivate()
        {
            _keyboard = false;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE));
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var cur = child;
            while (cur != null)
            {
                if (cur is T t) return t;
                cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(cur)
                    : LogicalTreeHelper.GetParent(cur);
            }
            return null;
        }

        // ---------- Win32 ----------

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        // Oculta la ventana de Alt+Tab y evita que robe el foco
        private void MakeToolWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        }
    }
}
