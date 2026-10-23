using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace DynamicIsland
{
    /// <summary>Un acceso rápido del lanzador (app, archivo o carpeta).</summary>
    public sealed class LaunchItem
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public ImageSource? Icon { get; init; }
    }

    /// <summary>Accesos rápidos a apps, archivos y carpetas (sin límite de espacios). Se guardan entre sesiones.</summary>
    public sealed class LauncherStore
    {
        public ObservableCollection<LaunchItem> Items { get; } = new();

        public void Load()
        {
            foreach (var p in AppSettings.Current.Launcher)
                if (File.Exists(p) || Directory.Exists(p)) Items.Add(Create(p));
        }

        public void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            if (Items.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            Items.Add(Create(path));
            Save();
        }

        public void Remove(LaunchItem item)
        {
            Items.Remove(item);
            Save();
        }

        public void Move(LaunchItem item, int newIndex)
        {
            int old = Items.IndexOf(item);
            if (old < 0 || newIndex < 0 || newIndex >= Items.Count || old == newIndex) return;
            Items.Move(old, newIndex);
            Save();
        }

        public static void Launch(LaunchItem item)
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch { }
        }

        private void Save()
        {
            AppSettings.Current.Launcher = Items.Select(i => i.Path).ToList();
            AppSettings.Current.Save();
        }

        private static LaunchItem Create(string path)
        {
            string name = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) name = path; // raíz de una unidad, p. ej. "D:\"
            return new LaunchItem { Path = path, Name = name, Icon = ShellIcons.Get(path) };
        }
    }
}
