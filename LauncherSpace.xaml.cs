using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DynamicIsland
{
    /// <summary>Espacio "Apps": accesos rápidos a apps, archivos y carpetas.</summary>
    public partial class LauncherSpace : UserControl
    {
        public LauncherSpace()
        {
            InitializeComponent();
            Tiles.ItemsSource = IslandTools.Launcher.Items;
            IslandTools.Launcher.Items.CollectionChanged += (_, _) => UpdateEmpty();
            UpdateEmpty();
        }

        public int Count => IslandTools.Launcher.Items.Count;

        public void SetDropHint(bool visible) => DropHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        private void UpdateEmpty() =>
            EmptyText.Visibility = IslandTools.Launcher.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private static LaunchItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as LaunchItem;

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (ItemOf(sender) is { } item) LauncherStore.Launch(item);
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) LauncherStore.Launch(item);
        }

        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item)
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true }); } catch { }
        }

        private void MoveLeft_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) IslandTools.Launcher.Move(item, IslandTools.Launcher.Items.IndexOf(item) - 1);
        }

        private void MoveRight_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) IslandTools.Launcher.Move(item, IslandTools.Launcher.Items.IndexOf(item) + 1);
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) IslandTools.Launcher.Remove(item);
        }

        private void AddFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Agregar al lanzador",
                Multiselect = true,
                DereferenceLinks = false, // conservar accesos directos (.lnk) tal cual
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Filter = "Apps y accesos directos|*.exe;*.lnk;*.url;*.appref-ms|Todos los archivos|*.*"
            };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FileNames) IslandTools.Launcher.Add(f);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Agregar carpetas al lanzador", Multiselect = true };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FolderNames) IslandTools.Launcher.Add(f);
        }
    }
}
