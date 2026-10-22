using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DynamicIsland
{
    /// <summary>
    /// Reconocimiento de texto (OCR) con el motor integrado de Windows 10/11, sin internet.
    /// Usa los idiomas instalados en Windows (español, inglés…).
    /// </summary>
    public static class OcrService
    {
        private static OcrEngine? _engine;
        private static bool _initialized;
        private static readonly object Gate = new();

        public static bool Available
        {
            get { Init(); return _engine != null; }
        }

        private static void Init()
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;
                try { _engine = OcrEngine.TryCreateFromUserProfileLanguages(); }
                catch { _engine = null; }
            }
        }

        /// <summary>Texto reconocido en una imagen (jpg, png, bmp, tiff, gif, webp…). Llamar desde un hilo de fondo.</summary>
        public static string RecognizeImageFile(string path)
        {
            Init();
            if (_engine == null) return "";
            return Task.Run(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenAsync(FileAccessMode.Read);
                return await RecognizeStreamAsync(stream);
            }).GetAwaiter().GetResult();
        }

        /// <summary>Texto reconocido en páginas escaneadas de un PDF (índices desde 0).</summary>
        public static string RecognizePdfPages(string path, IEnumerable<int> pages)
        {
            Init();
            if (_engine == null) return "";
            return Task.Run(async () =>
            {
                var sb = new StringBuilder();
                var file = await StorageFile.GetFileFromPathAsync(path);
                var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
                foreach (int i in pages)
                {
                    if (i < 0 || i >= doc.PageCount) continue;
                    using var page = doc.GetPage((uint)i);
                    using var ms = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1800 });
                    ms.Seek(0);
                    sb.Append(await RecognizeStreamAsync(ms)).Append('\n');
                }
                return sb.ToString();
            }).GetAwaiter().GetResult();
        }

        private static async Task<string> RecognizeStreamAsync(IRandomAccessStream stream)
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            if (w < 64 || h < 32) return ""; // íconos y miniaturas: no vale la pena

            var transform = new BitmapTransform();
            uint max = OcrEngine.MaxImageDimension;
            if (w > max || h > max)
            {
                double s = Math.Min((double)max / w, (double)max / h);
                transform.ScaledWidth = (uint)(w * s);
                transform.ScaledHeight = (uint)(h * s);
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

            OcrResult result = await _engine!.RecognizeAsync(bitmap);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
    }
}
