using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>Espacio "Portapapeles": historial con búsqueda, filtros y elementos fijados.</summary>
    public partial class ClipboardSpace : UserControl
    {
        /// <summary>El usuario hizo clic en la búsqueda: la isla debe aceptar teclado.</summary>
        public event Action? KeyboardRequested;

        private readonly ListCollectionView _view;
        private string _filter = "All";
        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        public ClipboardSpace()
        {
            InitializeComponent();

            // Vista propia para cada isla (cada monitor puede filtrar distinto)
            _view = new ListCollectionView(IslandTools.Clipboard.Items) { Filter = Matches };
            _view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.IsPinned), ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Time), ListSortDirection.Descending));
            _view.IsLiveSorting = true;
            _view.LiveSortingProperties.Add(nameof(ClipItem.IsPinned));
            _view.LiveSortingProperties.Add(nameof(ClipItem.Time));
            ClipList.ItemsSource = _view;

            IslandTools.Clipboard.Items.CollectionChanged += (_, _) => UpdateEmpty();
            _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); UpdateEmpty(); };
            UpdateEmpty();
        }

        public void SetActive(bool active)
        {
            if (!active)
            {
                SearchBox.Text = "";
                Keyboard.ClearFocus();
            }
            UpdateEmpty();
        }

        private bool Matches(object o)
        {
            if (o is not ClipItem c) return false;
            bool kind = _filter switch
            {
                "Text" => c.Kind == ClipKind.Text,
                "Link" => c.Kind == ClipKind.Link,
                "Image" => c.Kind == ClipKind.Image,
                "Files" => c.Kind == ClipKind.Files,
                "Pinned" => c.IsPinned,
                _ => true
            };
            if (!kind) return false;
            string q = SearchBox?.Text?.Trim() ?? "";
            if (q.Length == 0) return true;
            return c.Preview.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || c.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        }

        private void UpdateEmpty()
        {
            bool empty = _view.IsEmpty;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = IslandTools.Clipboard.Items.Count == 0
                ? (IslandTools.Clipboard.Enabled
                    ? "Lo que copies (texto, enlaces, imágenes o archivos) aparecerá aquí."
                    : "El historial del portapapeles está desactivado (actívalo desde la bandeja).")
                : "Nada coincide con la búsqueda.";
            if (!_statusTimer.IsEnabled)
                StatusText.Text = $"{IslandTools.Clipboard.Items.Count} elementos";
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        // ---------- Búsqueda y filtros ----------
        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Refresh();
            UpdateEmpty();
        }

        private void Search_MouseDown(object sender, MouseButtonEventArgs e)
        {
            KeyboardRequested?.Invoke();
            Dispatcher.BeginInvoke(new Action(() => { SearchBox.Focus(); Keyboard.Focus(SearchBox); }), DispatcherPriority.Input);
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            _filter = (sender as FrameworkElement)?.Tag as string ?? "All";
            if (_view == null) return; // durante InitializeComponent
            _view.Refresh();
            UpdateEmpty();
        }

        // ---------- Acciones ----------
        private void Row_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            if ((sender as FrameworkElement)?.Tag is ClipItem item)
            {
                IslandTools.Clipboard.CopyBack(item);
                ShowStatus("Copiado ✓  — listo para pegar");
            }
        }

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ClipItem item) IslandTools.Clipboard.TogglePin(item);
            _view.Refresh();
            e.Handled = true;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ClipItem item) IslandTools.Clipboard.Remove(item);
            e.Handled = true;
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ClipItem item)
            {
                string url = item.Text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + item.Text : item.Text;
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
            e.Handled = true;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            IslandTools.Clipboard.ClearUnpinned();
            UpdateEmpty();
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var cur = child;
            while (cur != null)
            {
                if (cur is T t) return t;
                cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
            }
            return null;
        }
    }
}
