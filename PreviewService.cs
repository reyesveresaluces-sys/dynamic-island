using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DynamicIsland
{
    public enum PreviewKind { None, Image, Pdf, Text, Thumbnail }

    public sealed class PreviewData
    {
        public PreviewKind Kind { get; init; }
        public ImageSource? Image { get; init; }
        public string Text { get; init; } = "";
        public bool Monospace { get; init; }
        public string Info { get; init; } = "";
        public string PageInfo { get; init; } = "";
    }

    /// <summary>Prepara la vista previa de un archivo (estilo Vista rápida de macOS).</summary>
    public static class PreviewService
    {
        private const int MaxPreviewChars = 60_000;

        private static readonly HashSet<string> WpfImages = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic"
        };

        public static async Task<PreviewData> LoadAsync(string path, IReadOnlyList<string> terms, CancellationToken ct)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string info = Describe(path, ext);

            try
            {
                if (WpfImages.Contains(ext))
                {
                    var img = await Task.Run(() => LoadImage(path), ct);
                    if (img != null) return new PreviewData { Kind = PreviewKind.Image, Image = img, Info = info };
                }
                else if (ext == ".pdf")
                {
                    var (page, count) = await Task.Run(() => TextExtractor.FindPdfPage(path, terms), ct);
                    ct.ThrowIfCancellationRequested();
                    var img = await RenderPdfPageAsync(path, page);
                    if (img != null)
                        return new PreviewData
                        {
                            Kind = PreviewKind.Pdf,
                            Image = img,
                            Info = info,
                            PageInfo = $"Página {page + 1} de {count}"
                        };
                }
                else if (TextExtractor.IsSupported(ext))
                {
                    string text = await Task.Run(() => TextExtractor.Extract(path, ext, keepLines: true), ct);
                    if (text.Length > MaxPreviewChars) text = text[..MaxPreviewChars] + "\n…";
                    if (text.Length > 0)
                        return new PreviewData { Kind = PreviewKind.Text, Text = text, Monospace = TextExtractor.IsCode(ext), Info = info };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* si falla, mostramos la miniatura de Windows */ }

            ct.ThrowIfCancellationRequested();
            var thumb = await RunSta(() => ShellThumbnail(path, 512));
            return new PreviewData { Kind = thumb != null ? PreviewKind.Thumbnail : PreviewKind.None, Image = thumb, Info = info };
        }

        private static string Describe(string path, string ext)
        {
            try
            {
                var f = new FileInfo(path);
                string kind = ext.Length > 1 ? ext[1..].ToUpperInvariant() : "Archivo";
                if (ext == ".lnk") kind = "Aplicación";
                string when = f.LastWriteTime.ToString("d MMM yyyy, HH:mm", new CultureInfo("es-MX"));
                return $"{kind}  ·  {FormatSize(f.Length)}  ·  Modificado {when}";
            }
            catch { return ""; }
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
        }

        private static ImageSource? LoadImage(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo bloqueado
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.DecodePixelWidth = 1100;
            img.StreamSource = fs;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private static async Task<ImageSource?> RenderPdfPageAsync(string path, int pageIndex)
        {
            var bytes = await Task.Run(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
                if (doc.PageCount == 0) return null;
                using var page = doc.GetPage((uint)Math.Min(pageIndex, (int)doc.PageCount - 1));
                using var ras = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1100 });
                ras.Seek(0);
                using var s = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                return ms.ToArray();
            });
            if (bytes == null) return null;

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }

        // ---------- Miniatura de Windows (videos, Office, etc.) ----------

        private static Task<T?> RunSta<T>(Func<T?> func) where T : class
        {
            var tcs = new TaskCompletionSource<T?>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(func()); }
                catch { tcs.SetResult(null); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private static ImageSource? ShellThumbnail(string path, int size)
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            try
            {
                const int SIIGBF_BIGGERSIZEOK = 0x1;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
    }
}
