using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

        /// <summary>Tipo de archivo con el que se agrupa la lista (Documentos, Hojas de cálculo…).</summary>
        public string Group { get; init; } = "Otros";
        /// <summary>Orden fijo de los grupos.</summary>
        public int GroupOrder { get; init; } = 99;
        /// <summary>Posición original: conserva la relevancia dentro de cada grupo.</summary>
        public int Order { get; init; }
    }

    /// <summary>Clasificación por tipo de archivo para separar los resultados.</summary>
    public static class FileKinds
    {
        private static readonly (int order, string label, string[] ext)[] Table =
        {
            (1, "Carpetas",            new[] { "\\" }),
            (2, "Documentos",          new[] { ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf", ".txt", ".md", ".epub", ".pages" }),
            (3, "PDF",                 new[] { ".pdf" }),
            (4, "Hojas de cálculo",    new[] { ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".tsv", ".ods", ".numbers" }),
            (5, "Presentaciones",      new[] { ".ppt", ".pptx", ".pptm", ".odp", ".key" }),
            (6, "Imágenes",            new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".svg", ".ico" }),
            (7, "Planos y diseño",     new[] { ".dwg", ".dxf", ".psd", ".ai", ".indd", ".skp", ".rvt", ".cdr" }),
            (8, "Video",               new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg" }),
            (9, "Audio",               new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma" }),
            (10, "Código y datos",     new[] { ".cs", ".js", ".ts", ".py", ".java", ".kt", ".cpp", ".c", ".h", ".go", ".rs", ".php", ".rb", ".sql", ".json", ".xml", ".yml", ".yaml", ".html", ".htm", ".css", ".ps1", ".bat", ".cmd", ".sh", ".ini", ".log" }),
            (11, "Comprimidos",        new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".cab" }),
            (12, "Correos",            new[] { ".msg", ".eml", ".pst", ".ost" }),
            (13, "Programas",          new[] { ".exe", ".msi", ".appx", ".dll" })
        };

        /// <summary>Grupo y orden de un resultado.</summary>
        public static (string label, int order) Of(string path, ResultKind kind)
        {
            if (kind == ResultKind.App) return ("Aplicaciones", 0);

            try
            {
                if (Directory.Exists(path)) return ("Carpetas", 1);
            }
            catch { }

            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext.Length > 0)
                foreach (var (order, label, list) in Table)
                    if (Array.IndexOf(list, ext) >= 0) return (label, order);

            return ("Otros", 99);
        }
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
        private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
        private CancellationTokenSource? _previewCts;
        private int _version;
        private string _indexStatus = "";
        private bool _indexBusy;

        private const double NarrowWidth = 780, WideWidth = 1240;

        public SearchWindow()
        {
            InitializeComponent();
            _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(Query.Text); };
            _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); LoadPreview(); };
            SizeChanged += (_, e) => { if (IsVisible && e.WidthChanged) Recenter(); };
            IsVisibleChanged += (_, _) => { if (!IsVisible) _previewCts?.Cancel(); };
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

        /// <summary>Al ensancharse para la vista previa, vuelve a centrarse sin moverse verticalmente.</summary>
        private void Recenter()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(hwnd, out var r)) return;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var area = screen.WorkingArea;
            int width = r.Right - r.Left;
            int x = area.Left + (area.Width - width) / 2;
            SetWindowPos(hwnd, HWND_TOPMOST, x, r.Top, 0, 0, SWP_NOSIZE);
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
                _all.Clear();
                TypeChips.Children.Clear();
                ResultsArea.Visibility = Visibility.Collapsed;
                Width = NarrowWidth;
                return;
            }

            Task.Run(() =>
            {
                try { return SearchIndex.Search(text); }
                catch { return new List<SearchResult>(); }
            }).ContinueWith(t =>
            {
                if (version != _version) return; // ya escribieron otra cosa
                var items = t.Result.Select((r, i) => ToItem(r, i)).ToList();
                _all = items;
                _typeFilter = null;
                BuildChips();
                ShowResults();
                EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (items.Count == 0 && _indexBusy) EmptyText.Text = "Sin resultados todavía — el índice se sigue construyendo";
                else EmptyText.Text = "Sin resultados";
                CountText.Text = _indexBusy ? "" : _indexStatus;
                ResultsArea.Visibility = Visibility.Visible;

                bool any = items.Count > 0;
                PreviewPane.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                ResultsGrid.Height = any ? 500 : 56;
                ListColumn.Width = any ? new GridLength(460) : new GridLength(1, GridUnitType.Star);
                Width = any ? WideWidth : NarrowWidth;

                if (any) SelectFirst();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------- Clasificación por tipo ----------------

        private List<ResultItem> _all = new();
        private string? _typeFilter;

        /// <summary>Arma la lista agrupada por tipo (y filtrada si hay un tipo elegido).</summary>
        private void ShowResults()
        {
            var shown = _typeFilter == null ? _all : _all.Where(i => i.Group == _typeFilter).ToList();
            var view = new System.Windows.Data.ListCollectionView(shown);
            // Un solo tipo a la vista no necesita encabezados
            if (_typeFilter == null)
                view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ResultItem.Group)));
            view.SortDescriptions.Add(new SortDescription(nameof(ResultItem.GroupOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(ResultItem.Order), ListSortDirection.Ascending));
            Results.ItemsSource = view;
        }

        private void SelectFirst()
        {
            if (Results.Items.Count == 0) return;
            Results.SelectedIndex = 0;
            Results.ScrollIntoView(Results.Items[0]);
            LoadPreview();
        }

        /// <summary>Fichas de tipo: "Todo" y un botón por cada tipo presente, con su conteo.</summary>
        private void BuildChips()
        {
            TypeChips.Children.Clear();
            if (_all.Count == 0) return;

            var groups = _all.GroupBy(i => i.Group)
                             .Select(g => (Label: g.Key, Count: g.Count(), Order: g.Min(i => i.GroupOrder)))
                             .OrderBy(g => g.Order)
                             .ToList();

            TypeChips.Children.Add(Chip("Todo", _all.Count, null));
            if (groups.Count > 1)
                foreach (var g in groups) TypeChips.Children.Add(Chip(g.Label, g.Count, g.Label));
        }

        private Border Chip(string label, int count, string? filter)
        {
            bool on = _typeFilter == filter;
            var text = new TextBlock
            {
                Text = $"{label}  {count}",
                FontSize = 11.5,
                Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2))
            };
            var chip = new Border
            {
                Background = new SolidColorBrush(on ? Color.FromRgb(0x0A, 0x5B, 0xD3) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Child = text,
                Tag = filter
            };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                _typeFilter = filter;
                BuildChips();
                ShowResults();
                SelectFirst();
                e.Handled = true;
            };
            return chip;
        }

        /// <summary>Tab pasa al siguiente tipo.</summary>
        private void CycleType(int direction)
        {
            var order = new List<string?> { null };
            order.AddRange(_all.GroupBy(i => i.Group)
                               .Select(g => (Label: g.Key, Order: g.Min(i => i.GroupOrder)))
                               .OrderBy(g => g.Order).Select(g => (string?)g.Label));
            if (order.Count <= 1) return;
            int i = order.IndexOf(_typeFilter);
            _typeFilter = order[((i < 0 ? 0 : i) + direction + order.Count) % order.Count];
            BuildChips();
            ShowResults();
            SelectFirst();
        }

        private static ResultItem ToItem(SearchResult r, int order)
        {
            string folder = System.IO.Path.GetDirectoryName(r.Path) ?? "";
            string when = r.Modified.ToString("d MMM yyyy", new CultureInfo("es-MX"));
            var (group, groupOrder) = FileKinds.Of(r.Path, r.Kind);
            return new ResultItem
            {
                Path = r.Path,
                Name = r.Name,
                Icon = ShellIcons.Get(r.Path),
                Group = group,
                GroupOrder = groupOrder,
                Order = order,
                KindLabel = r.Kind switch
                {
                    ResultKind.App => "App",
                    ResultKind.Content when TextExtractor.IsImage(System.IO.Path.GetExtension(r.Path).ToLowerInvariant()) => "Texto en imagen",
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

        // ---------------- Vista previa ----------------

        private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        private List<string> QueryTerms() =>
            Query.Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(t => t.Trim('"', '*', '(', ')', ':', '^', '-', '+'))
                 .Where(t => t.Length > 1)
                 .ToList();

        private async void LoadPreview()
        {
            _previewCts?.Cancel();
            if (Results.SelectedItem is not ResultItem item) return;

            var cts = new CancellationTokenSource();
            _previewCts = cts;
            var terms = QueryTerms();

            PreviewTitle.Text = item.Name;
            PreviewPath.Text = item.Path;
            PreviewInfo.Text = "";
            PreviewImage.Source = null;
            PreviewText.Visibility = Visibility.Collapsed;
            PageBadge.Visibility = Visibility.Collapsed;
            PreviewLoading.Text = "Cargando vista previa…";
            PreviewLoading.Visibility = Visibility.Visible;

            PreviewData data;
            try
            {
                data = await PreviewService.LoadAsync(item.Path, terms, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { data = new PreviewData(); }
            if (cts.IsCancellationRequested || !ReferenceEquals(Results.SelectedItem, item)) return;

            PreviewInfo.Text = data.Info;
            PreviewLoading.Visibility = Visibility.Collapsed;

            switch (data.Kind)
            {
                case PreviewKind.Image:
                case PreviewKind.Thumbnail:
                    PreviewImage.Source = data.Image;
                    break;
                case PreviewKind.Pdf:
                    PreviewImage.Source = data.Image;
                    PageText.Text = data.PageInfo;
                    PageBadge.Visibility = Visibility.Visible;
                    break;
                case PreviewKind.Text:
                    ShowText(data.Text, terms, data.Monospace);
                    break;
                default:
                    PreviewImage.Source = item.Icon;
                    break;
            }
        }

        /// <summary>Muestra el texto con las palabras buscadas resaltadas y salta a la primera.</summary>
        private void ShowText(string text, IReadOnlyList<string> terms, bool mono)
        {
            var doc = new FlowDocument
            {
                FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = mono ? 12 : 13,
                PagePadding = new Thickness(0),
                LineHeight = mono ? 17 : 19
            };
            var highlight = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xD6, 0x0A));
            highlight.Freeze();
            var cmp = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            Run? first = null;

            foreach (var block in text.Split("\n\n"))
            {
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                var lines = block.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    int pos = 0;
                    while (pos < line.Length)
                    {
                        // Siguiente coincidencia de cualquiera de los términos
                        int best = -1, len = 0;
                        foreach (var t in terms)
                        {
                            int idx = cmp.IndexOf(line, t, pos, opts);
                            if (idx >= 0 && (best < 0 || idx < best)) { best = idx; len = t.Length; }
                        }
                        if (best < 0)
                        {
                            para.Inlines.Add(new Run(line[pos..]));
                            break;
                        }
                        if (best > pos) para.Inlines.Add(new Run(line[pos..best]));
                        var hit = new Run(line.Substring(best, Math.Min(len, line.Length - best)))
                        {
                            Background = highlight,
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.SemiBold
                        };
                        para.Inlines.Add(hit);
                        first ??= hit;
                        pos = best + Math.Max(len, 1);
                    }
                    if (li < lines.Length - 1) para.Inlines.Add(new LineBreak());
                }
                doc.Blocks.Add(para);
            }

            PreviewText.Document = doc;
            PreviewText.Visibility = Visibility.Visible;
            PreviewText.ScrollToHome();
            if (first != null)
            {
                var target = first;
                Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), DispatcherPriority.Loaded);
            }
        }

        private void Preview_Click(object sender, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem is ResultItem item) Open(item, reveal: false);
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
                case Key.Tab when _all.Count > 0:
                    CycleType(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
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
