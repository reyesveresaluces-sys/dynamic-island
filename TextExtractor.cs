using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DynamicIsland
{
    /// <summary>Extrae el texto de documentos para indexarlo (como AnyTXT Searcher) y para la vista previa.</summary>
    public static class TextExtractor
    {
        private const int MaxChars = 1_000_000;

        private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".xml", ".yml", ".yaml", ".ini", ".cfg", ".conf", ".toml", ".properties", ".env", ".sql",
            ".bat", ".cmd", ".ps1", ".sh", ".cs", ".xaml", ".csproj", ".sln", ".vb", ".fs", ".java", ".kt", ".kts",
            ".gradle", ".py", ".rb", ".php", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".css", ".scss", ".less", ".vue",
            ".svelte", ".go", ".rs", ".swift", ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".r", ".lua", ".pl", ".dart",
            ".tex", ".bib", ".reg", ".gitignore", ".dockerfile", ".asm", ".ino", ".log", ".csv", ".tsv"
        };

        private static readonly HashSet<string> PlainText = new(Code, StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".srt", ".vtt", ".nfo"
        };

        private static readonly HashSet<string> Markup = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml", ".svg" };
        private static readonly HashSet<string> OpenXml = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".ppsx" };
        private static readonly HashSet<string> OpenDoc = new(StringComparer.OrdinalIgnoreCase) { ".odt", ".ods", ".odp" };
        private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif", ".webp", ".jfif", ".heic"
        };

        public static bool IsSupported(string ext) =>
            PlainText.Contains(ext) || Markup.Contains(ext) || OpenXml.Contains(ext) || OpenDoc.Contains(ext)
            || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".epub", StringComparison.OrdinalIgnoreCase);

        public static bool IsImage(string ext) => Images.Contains(ext);
        public static bool IsCode(string ext) => Code.Contains(ext);

        public static long MaxSize(string ext) =>
            PlainText.Contains(ext) || Markup.Contains(ext) || ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)
                ? 16L * 1024 * 1024
                : 100L * 1024 * 1024;

        /// <summary>Texto del archivo. Con keepLines conserva los saltos de línea (para la vista previa).</summary>
        public static string Extract(string path, string ext, bool keepLines = false)
        {
            string text;
            if (PlainText.Contains(ext)) text = ReadText(path);
            else if (Markup.Contains(ext)) text = StripTags(ReadText(path));
            else if (ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)) text = StripRtf(ReadText(path));
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) text = ReadPdf(path, null);
            else if (OpenXml.Contains(ext)) text = ReadZipXml(path, IsOpenXmlTextPart);
            else if (OpenDoc.Contains(ext)) text = ReadZipXml(path, n => n.Equals("content.xml", StringComparison.OrdinalIgnoreCase));
            else if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
                text = StripTags(ReadZipXml(path, n => n.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".html", StringComparison.OrdinalIgnoreCase)));
            else text = "";

            return Normalize(text, keepLines);
        }

        /// <summary>Texto de un PDF y las páginas que casi no tienen texto (probablemente escaneadas).</summary>
        public static string ExtractPdf(string path, out List<int> scannedPages)
        {
            scannedPages = new List<int>();
            return Normalize(ReadPdf(path, scannedPages), false);
        }

        /// <summary>Índice (desde 0) y total de páginas: la primera página que contiene alguno de los términos.</summary>
        public static (int page, int count) FindPdfPage(string path, IReadOnlyList<string> terms)
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
            int count = doc.NumberOfPages;
            if (terms.Count == 0) return (0, count);
            var cmp = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
            const System.Globalization.CompareOptions opts =
                System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
            int i = 0;
            foreach (var page in doc.GetPages())
            {
                if (i >= 300) break;
                string t = page.Text;
                if (terms.Any(term => cmp.IndexOf(t, term, opts) >= 0)) return (i, count);
                i++;
            }
            return (0, count);
        }

        private static string Normalize(string text, bool keepLines)
        {
            if (keepLines)
            {
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                text = Regex.Replace(text, @"[ \t\f\v\u00A0]+", " ");
                text = Regex.Replace(text, @" ?\n ?", "\n");
                text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
            }
            else
            {
                text = Regex.Replace(text, @"\s+", " ").Trim();
            }
            return text.Length > MaxChars ? text[..MaxChars] : text;
        }

        // ---------- Texto plano ----------
        private static string ReadText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return "";

            // BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

            // Binario disfrazado: muchos bytes nulos
            int probe = Math.Min(bytes.Length, 8192), zeros = 0;
            for (int i = 0; i < probe; i++) if (bytes[i] == 0) zeros++;
            if (zeros > probe / 10) return "";

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes); // archivos ANSI viejos (acentos)
            }
        }

        private static string StripTags(string html)
        {
            html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(br|/p|/div|/li|/h\d|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(html);
        }

        private static string StripRtf(string rtf)
        {
            var sb = new StringBuilder();
            rtf = Regex.Replace(rtf, @"\\'([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            rtf = Regex.Replace(rtf, @"\{\\\*[^{}]*\}", " ");
            rtf = Regex.Replace(rtf, @"\\(par|line)\b ?", "\n");
            rtf = Regex.Replace(rtf, @"\\tab\b ?", " ");
            rtf = Regex.Replace(rtf, @"\\[a-zA-Z]+-?\d* ?", "");
            foreach (char c in rtf) if (c != '{' && c != '}') sb.Append(c);
            return sb.ToString();
        }

        // ---------- PDF ----------
        private static string ReadPdf(string path, List<int>? scannedPages)
        {
            var sb = new StringBuilder();
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
            int i = 0;
            foreach (var page in doc.GetPages())
            {
                string t = page.Text;
                if (scannedPages != null && t.Trim().Length < 25) scannedPages.Add(i);
                sb.Append(t).Append('\n');
                if (sb.Length > MaxChars) break;
                i++;
            }
            return sb.ToString();
        }

        // ---------- Office (docx/xlsx/pptx) y OpenDocument ----------
        private static bool IsOpenXmlTextPart(string name)
        {
            name = name.Replace('\\', '/');
            return name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml"))
                || (name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml"))
                || name.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml"))
                || (name.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml"))
                || (name.StartsWith("ppt/notesSlides/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml"));
        }

        // Fin de párrafo o fila → salto de línea; fin de celda o tabulador → espacio
        private static readonly HashSet<string> LineBreaks = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "br", "tr", "si", "row", "h", "list-item", "line-break", "div", "li"
        };
        private static readonly HashSet<string> SpaceBreaks = new(StringComparer.OrdinalIgnoreCase)
        {
            "tab", "tc", "c", "table-cell", "s", "td"
        };

        private static string ReadZipXml(string path, Func<string, bool> partFilter)
        {
            var sb = new StringBuilder();
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => partFilter(e.FullName)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Length > 200L * 1024 * 1024) continue;
                using var stream = entry.Open();
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
                using var reader = XmlReader.Create(stream, settings);
                try
                {
                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                                sb.Append(reader.Value);
                                break;
                            case XmlNodeType.EndElement:
                                AppendBreak(sb, reader.LocalName);
                                break;
                            case XmlNodeType.Element:
                                if (reader.IsEmptyElement) AppendBreak(sb, reader.LocalName);
                                break;
                        }
                        if (sb.Length > MaxChars) return sb.ToString();
                    }
                }
                catch (XmlException) { /* parte dañada: seguimos con la siguiente */ }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void AppendBreak(StringBuilder sb, string name)
        {
            if (LineBreaks.Contains(name)) sb.Append('\n');
            else if (SpaceBreaks.Contains(name)) sb.Append(' ');
        }
    }
}
