using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>Un resultado ya preparado para mostrarse.</summary>
    public sealed class ResultItem
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string KindLabel { get; init; } = "";
        public ImageSource? Icon { get; init; }
    }

    /// <summary>TextBlock que resalta el texto entre \u0001 y \u0002 (fragmentos de la búsqueda).</summary>
    public class HighlightTextBlock : TextBlock
    {
        private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));

        public static readonly DependencyProperty MarkedTextProperty = DependencyProperty.Register(
            nameof(MarkedText), typeof(string), typeof(HighlightTextBlock),
            new PropertyMetadata("", (d, _) => ((HighlightTextBlock)d).Rebuild()));

        public string MarkedText
        {
            get => (string)GetValue(MarkedTextProperty);
            set => SetValue(MarkedTextProperty, value);
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private void Rebuild()
        {
            Inlines.Clear();
            var sb = new StringBuilder();
            bool on = false;
            void Flush()
            {
                if (sb.Length == 0) return;
                var run = new Run(sb.ToString());
                if (on)
                {
                    run.FontWeight = FontWeights.SemiBold;
                    run.Foreground = HighlightBrush;
                }
                Inlines.Add(run);
                sb.Clear();
            }
            foreach (char c in MarkedText ?? "")
            {
                if (c == '\u0001' || c == '\u0002')
                {
                    Flush();
                    on = c == '\u0001';
                }
                else sb.Append(c);
            }
            Flush();
        }
    }

    public partial class SearchWindow : Window
    {
        public string HotkeyLabel { get; private set; } = "";
        public bool HotkeyRegistered { get; private set; }

        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(130) };
        private int _version;
        private string _indexStatus = "";
        private bool _indexBusy;

        public SearchWindow()
        {
            InitializeComponent();
            _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(Query.Text); };
            Deactivated += (_, _) => HideBar();
            PreviewKeyDown += OnPreviewKeyDown;
            SourceInitialized += (_, _) => { MakeToolWindow(); RegisterHotkey(); };
            Closed += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotkeyId);
            };
        }

        /// <summary>Crea la ventana (oculta) para poder registrar el atajo global.</summary>
        public void Initialize() => new WindowInteropHelper(this).EnsureHandle();

        public void SetIndexStatus(string status, bool busy)
        {
            _indexStatus = status;
            _indexBusy = busy;
            BusyText.Text = busy ? status : "";
            CountText.Text = busy ? "" : status;
        }

        // ---------------- Mostrar / ocultar ----------------

        public void Toggle()
        {
            if (IsVisible && IsActive) HideBar();
            else ShowBar();
        }

        public void ShowBar()
        {
            Opacity = 0;
            Show();
            PositionOnActiveScreen();
            Opacity = 1;

            var hwnd = new WindowInteropHelper(this).Handle;
            Activate();
            SetForegroundWindow(hwnd);
            Query.Focus();
            Keyboard.Focus(Query);
            Query.SelectAll();
            if (!string.IsNullOrWhiteSpace(Query.Text)) RunSearch(Query.Text);
        }

        public void HideBar()
        {
            if (IsVisible) Hide();
        }

        private void PositionOnActiveScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var area = screen.WorkingArea;
            if (!GetWindowRect(hwnd, out var r)) return;
            int width = r.Right - r.Left;
            int x = area.Left + (area.Width - width) / 2;
            int y = area.Top + (int)(area.Height * 0.16);
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);
        }

        // ---------------- Búsqueda ----------------

        private void Query_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            _debounce.Start();
        }

        private void RunSearch(string text)
        {
            int version = ++_version;
            if (string.IsNullOrWhiteSpace(text))
            {
                Results.ItemsSource = null;
                ResultsArea.Visibility = Visibility.Collapsed;
                return;
            }

            Task.Run(() =>
            {
                try { return SearchIndex.Search(text); }
                catch { return new List<SearchResult>(); }
            }).ContinueWith(t =>
            {
                if (version != _version) return; // ya escribieron otra cosa
                var items = t.Result.Select(ToItem).ToList();
                Results.ItemsSource = items;
                EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (items.Count == 0 && _indexBusy) EmptyText.Text = "Sin resultados todavía — el índice se sigue construyendo";
                else EmptyText.Text = "Sin resultados";
                CountText.Text = _indexBusy ? "" : _indexStatus;
                ResultsArea.Visibility = Visibility.Visible;
                if (items.Count > 0)
                {
                    Results.SelectedIndex = 0;
                    Results.ScrollIntoView(items[0]);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static ResultItem ToItem(SearchResult r)
        {
            string folder = System.IO.Path.GetDirectoryName(r.Path) ?? "";
            string when = r.Modified.ToString("d MMM yyyy", new CultureInfo("es-MX"));
            return new ResultItem
            {
                Path = r.Path,
                Name = r.Name,
                Icon = ShellIcons.Get(r.Path),
                KindLabel = r.Kind switch
                {
                    ResultKind.App => "App",
                    ResultKind.Content => "Contenido",
                    _ => "Archivo"
                },
                Subtitle = r.Kind switch
                {
                    ResultKind.App => "Aplicación",
                    ResultKind.Content => string.IsNullOrWhiteSpace(r.Snippet) ? folder : r.Snippet,
                    _ => $"{folder}  ·  {when}"
                }
            };
        }

        // ---------------- Teclado y mouse ----------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            int count = Results.Items.Count;
            switch (e.Key)
            {
                case Key.Escape:
                    HideBar();
                    e.Handled = true;
                    break;
                case Key.Down when count > 0:
                    Results.SelectedIndex = Math.Min(count - 1, Results.SelectedIndex + 1);
                    Results.ScrollIntoView(Results.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up when count > 0:
                    Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
                    Results.ScrollIntoView(Results.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (Results.SelectedItem is ResultItem item)
                        Open(item, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                    e.Handled = true;
                    break;
            }
        }

        private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem is ResultItem item)
                Open(item, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }

        private void Open(ResultItem item, bool reveal)
        {
            HideBar();
            try
            {
                if (reveal)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            catch
            {
                // Si no hay programa asociado, al menos mostrarlo en la carpeta
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true }); } catch { }
            }
        }

        // ---------------- Atajo global (Alt+Espacio) ----------------

        private const int HotkeyId = 0xB001;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        private const uint VK_SPACE = 0x20;

        private void RegisterHotkey()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

            if (RegisterHotKey(hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_SPACE))
            {
                HotkeyRegistered = true;
                HotkeyLabel = "Alt+Espacio";
            }
            else if (RegisterHotKey(hwnd, HotkeyId, MOD_WIN | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE))
            {
                HotkeyRegistered = true;
                HotkeyLabel = "Win+Shift+Espacio"; // Alt+Espacio lo ocupa otra app (p. ej. PowerToys Run)
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Toggle();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---------------- Win32 ----------------

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private void MakeToolWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        }
    }
}
