using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland
{
    public enum ClipKind { Text, Link, Image, Files }

    /// <summary>Un elemento del historial del portapapeles.</summary>
    public sealed class ClipItem : INotifyPropertyChanged
    {
        public ClipKind Kind { get; init; }
        public string Text { get; init; } = "";
        public BitmapSource? Image { get; init; }
        public BitmapSource? Thumb { get; init; }
        public string[] Files { get; init; } = Array.Empty<string>();
        public string ImageFile { get; set; } = "";

        private DateTime _time = DateTime.Now;
        public DateTime Time { get => _time; set { _time = value; OnChanged(); OnChanged(nameof(TimeText)); } }

        private bool _pinned;
        public bool IsPinned { get => _pinned; set { _pinned = value; OnChanged(); OnChanged(nameof(PinGlyph)); } }

        public string Preview => Kind switch
        {
            ClipKind.Image => Image != null ? $"Imagen · {Image.PixelWidth}×{Image.PixelHeight}" : "Imagen",
            ClipKind.Files => Files.Length == 1 ? Path.GetFileName(Files[0]) : $"{Files.Length} archivos: " + string.Join(", ", Files.Take(3).Select(Path.GetFileName)),
            _ => Collapse(Text, 220)
        };

        public string KindLabel => Kind switch
        {
            ClipKind.Link => "Enlace",
            ClipKind.Image => "Imagen",
            ClipKind.Files => "Archivos",
            _ => "Texto"
        };

        public string Glyph => Kind switch
        {
            ClipKind.Link => "",
            ClipKind.Image => "",
            ClipKind.Files => "",
            _ => ""
        };

        public string PinGlyph => IsPinned ? "" : "";
        public Visibility ThumbVisibility => Thumb != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GlyphVisibility => Thumb == null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinkVisibility => Kind == ClipKind.Link ? Visibility.Visible : Visibility.Collapsed;
        public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Time.ToString("dd/MM");

        public bool SameContent(ClipItem other) =>
            Kind == other.Kind && Kind switch
            {
                ClipKind.Files => Files.SequenceEqual(other.Files, StringComparer.OrdinalIgnoreCase),
                ClipKind.Image => Image != null && other.Image != null && Image.PixelWidth == other.Image.PixelWidth
                                  && Image.PixelHeight == other.Image.PixelHeight && ImageHash == other.ImageHash,
                _ => Text == other.Text
            };

        private long? _hash;
        private long ImageHash => _hash ??= ComputeHash(Image);

        private static long ComputeHash(BitmapSource? img)
        {
            if (img == null) return 0;
            try
            {
                var small = new FormatConvertedBitmap(new TransformedBitmap(img, new ScaleTransform(
                    16.0 / img.PixelWidth, 16.0 / img.PixelHeight)), PixelFormats.Gray8, null, 0);
                var px = new byte[small.PixelWidth * small.PixelHeight];
                small.CopyPixels(px, small.PixelWidth, 0);
                long h = 17;
                foreach (var b in px) h = h * 31 + b;
                return h;
            }
            catch { return img.GetHashCode(); }
        }

        private static string Collapse(string s, int max)
        {
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s[..max] + "…" : s;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Historial del portapapeles: texto, enlaces, imágenes y archivos; con elementos fijados que se guardan.
    /// Respeta las apps que piden no registrar su contenido (gestores de contraseñas).
    /// </summary>
    public sealed class ClipboardService
    {
        public ObservableCollection<ClipItem> Items { get; } = new();
        public bool Enabled { get; set; } = true;
        public event Action<ClipItem>? Copied;

        private const int MaxItems = 60, MaxImages = 12;
        private static readonly string ClipDir = Path.Combine(SearchIndex.DataDir, "clips");
        private MessageWindow? _window;
        private bool _ignoreNext;

        [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        public void Start()
        {
            LoadPinned();
            _window = new MessageWindow("DynamicIsland.Clipboard");
            _window.Message += (msg, _, _) =>
            {
                if (msg != WM_CLIPBOARDUPDATE) return false;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Capture(0)), DispatcherPriority.Background);
                return true;
            };
            AddClipboardFormatListener(_window.Handle);
        }

        public void Stop()
        {
            if (_window != null) RemoveClipboardFormatListener(_window.Handle);
            _window?.Dispose();
        }

        // ---------- Captura ----------

        private void Capture(int attempt)
        {
            if (_ignoreNext) { _ignoreNext = false; return; }
            if (!Enabled) return;

            ClipItem? item;
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null || IsExcluded(data)) return;
                item = Read(data);
            }
            catch (COMException)
            {
                // Otra app tiene abierto el portapapeles: reintentar
                if (attempt < 4)
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                    t.Tick += (_, _) => { t.Stop(); Capture(attempt + 1); };
                    t.Start();
                }
                return;
            }
            catch { return; }

            if (item != null) Add(item);
        }

        private static bool IsExcluded(IDataObject data)
        {
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
                return true;
            try
            {
                if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
                    data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = ms.ToArray();
                    return BitConverter.ToInt32(b, 0) == 0;
                }
            }
            catch { }
            return false;
        }

        private static readonly Regex UrlRegex = new(@"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static ClipItem? Read(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                return new ClipItem { Kind = ClipKind.Files, Files = files };

            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (text.Length > 200_000) text = text[..200_000];
                bool link = UrlRegex.IsMatch(text.Trim());
                return new ClipItem { Kind = link ? ClipKind.Link : ClipKind.Text, Text = link ? text.Trim() : text };
            }

            if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib))
            {
                var img = Clipboard.GetImage();
                if (img == null) return null;
                img.Freeze();
                return new ClipItem { Kind = ClipKind.Image, Image = img, Thumb = MakeThumb(img) };
            }
            return null;
        }

        private static BitmapSource MakeThumb(BitmapSource img)
        {
            double s = Math.Min(1.0, 160.0 / Math.Max(img.PixelWidth, img.PixelHeight));
            BitmapSource t = s < 1 ? new TransformedBitmap(img, new ScaleTransform(s, s)) : img;
            if (!t.IsFrozen) t.Freeze();
            return t;
        }

        private void Add(ClipItem item)
        {
            var existing = Items.FirstOrDefault(i => i.SameContent(item));
            if (existing != null)
            {
                existing.Time = DateTime.Now;
                Items.Move(Items.IndexOf(existing), 0);
                return;
            }

            Items.Insert(0, item);

            // Límite de tamaño (los fijados no cuentan)
            var images = Items.Where(i => i.Kind == ClipKind.Image && !i.IsPinned).Skip(MaxImages).ToList();
            foreach (var i in images) Items.Remove(i);
            var extra = Items.Where(i => !i.IsPinned).Skip(MaxItems).ToList();
            foreach (var i in extra) Items.Remove(i);
        }

        // ---------- Acciones ----------

        /// <summary>Vuelve a poner el elemento en el portapapeles (listo para pegar).</summary>
        public void CopyBack(ClipItem item)
        {
            try
            {
                _ignoreNext = true;
                switch (item.Kind)
                {
                    case ClipKind.Image when item.Image != null:
                        Clipboard.SetImage(item.Image);
                        break;
                    case ClipKind.Files:
                        var sc = new StringCollection();
                        sc.AddRange(item.Files);
                        Clipboard.SetFileDropList(sc);
                        break;
                    default:
                        Clipboard.SetText(item.Text);
                        break;
                }
                item.Time = DateTime.Now;
                int idx = Items.IndexOf(item);
                if (idx > 0) Items.Move(idx, 0);
                Copied?.Invoke(item);
            }
            catch
            {
                _ignoreNext = false;
            }
        }

        public void TogglePin(ClipItem item)
        {
            item.IsPinned = !item.IsPinned;
            SavePinned();
        }

        public void Remove(ClipItem item)
        {
            Items.Remove(item);
            if (item.IsPinned) SavePinned();
        }

        public void ClearUnpinned()
        {
            foreach (var i in Items.Where(i => !i.IsPinned).ToList()) Items.Remove(i);
        }

        // ---------- Fijados (se guardan entre sesiones) ----------

        private void SavePinned()
        {
            try
            {
                Directory.CreateDirectory(ClipDir);
                var list = new System.Collections.Generic.List<PinnedClip>();
                foreach (var i in Items.Where(i => i.IsPinned))
                {
                    if (i.Kind == ClipKind.Image && i.Image != null && (i.ImageFile.Length == 0 || !File.Exists(i.ImageFile)))
                    {
                        i.ImageFile = Path.Combine(ClipDir, Guid.NewGuid().ToString("N") + ".png");
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(i.Image));
                        using var fs = File.Create(i.ImageFile);
                        enc.Save(fs);
                    }
                    list.Add(new PinnedClip
                    {
                        Kind = i.Kind.ToString(),
                        Text = i.Text,
                        ImageFile = i.ImageFile,
                        Files = i.Files.ToList(),
                        Time = i.Time
                    });
                }

                // Borrar imágenes de elementos que ya no están fijados
                var keep = list.Select(p => p.ImageFile).Where(f => f.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(ClipDir, "*.png"))
                    if (!keep.Contains(f)) { try { File.Delete(f); } catch { } }

                AppSettings.Current.PinnedClips = list;
                AppSettings.Current.Save();
            }
            catch { }
        }

        private void LoadPinned()
        {
            foreach (var p in AppSettings.Current.PinnedClips)
            {
                try
                {
                    Enum.TryParse(p.Kind, out ClipKind kind);
                    BitmapSource? img = null;
                    if (kind == ClipKind.Image)
                    {
                        if (!File.Exists(p.ImageFile)) continue;
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(p.ImageFile);
                        bi.EndInit();
                        bi.Freeze();
                        img = bi;
                    }
                    Items.Add(new ClipItem
                    {
                        Kind = kind,
                        Text = p.Text,
                        Files = p.Files.ToArray(),
                        Image = img,
                        Thumb = img != null ? MakeThumb(img) : null,
                        ImageFile = p.ImageFile,
                        Time = p.Time,
                        IsPinned = true
                    });
                }
                catch { }
            }
        }
    }
}
