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
    /// <summary>Extrae el texto de documentos para indexarlo (como AnyTXT Searcher).</summary>
    public static class TextExtractor
    {
        private const int MaxChars = 1_000_000;

        private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".json", ".xml", ".yml", ".yaml", ".ini", ".cfg",
            ".conf", ".toml", ".properties", ".env", ".sql", ".bat", ".cmd", ".ps1", ".sh",
            ".cs", ".xaml", ".csproj", ".sln", ".vb", ".fs", ".java", ".kt", ".kts", ".gradle", ".py", ".rb", ".php",
            ".js", ".jsx", ".ts", ".tsx", ".mjs", ".css", ".scss", ".less", ".vue", ".svelte", ".go", ".rs", ".swift",
            ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".r", ".lua", ".pl", ".dart", ".tex", ".bib", ".srt", ".vtt",
            ".nfo", ".reg", ".gitignore", ".dockerfile", ".asm", ".ino"
        };

        private static readonly HashSet<string> Markup = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml", ".svg" };
        private static readonly HashSet<string> OpenXml = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".ppsx" };
        private static readonly HashSet<string> OpenDoc = new(StringComparer.OrdinalIgnoreCase) { ".odt", ".ods", ".odp" };

        public static bool IsSupported(string ext) =>
            PlainText.Contains(ext) || Markup.Contains(ext) || OpenXml.Contains(ext) || OpenDoc.Contains(ext)
            || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".epub", StringComparison.OrdinalIgnoreCase);

        public static long MaxSize(string ext) =>
            PlainText.Contains(ext) || Markup.Contains(ext) || ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)
                ? 16L * 1024 * 1024
                : 100L * 1024 * 1024;

        public static string Extract(string path, string ext)
        {
            string text;
            if (PlainText.Contains(ext)) text = ReadText(path);
            else if (Markup.Contains(ext)) text = StripTags(ReadText(path));
            else if (ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)) text = StripRtf(ReadText(path));
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) text = ReadPdf(path);
            else if (OpenXml.Contains(ext)) text = ReadZipXml(path, IsOpenXmlTextPart);
            else if (OpenDoc.Contains(ext)) text = ReadZipXml(path, n => n.Equals("content.xml", StringComparison.OrdinalIgnoreCase));
            else if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
                text = StripTags(ReadZipXml(path, n => n.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".html", StringComparison.OrdinalIgnoreCase)));
            else text = "";

            text = Regex.Replace(text, @"\s+", " ").Trim();
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
            html = Regex.Replace(html, "<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(html);
        }

        private static string StripRtf(string rtf)
        {
            var sb = new StringBuilder();
            rtf = Regex.Replace(rtf, @"\\'([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            rtf = Regex.Replace(rtf, @"\{\\\*[^{}]*\}", " ");
            rtf = Regex.Replace(rtf, @"\\(par|line|tab)\b ?", " ");
            rtf = Regex.Replace(rtf, @"\\[a-zA-Z]+-?\d* ?", "");
            foreach (char c in rtf) if (c != '{' && c != '}') sb.Append(c);
            return sb.ToString();
        }

        // ---------- PDF ----------
        private static string ReadPdf(string path)
        {
            var sb = new StringBuilder();
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
            foreach (var page in doc.GetPages())
            {
                sb.Append(page.Text).Append(' ');
                if (sb.Length > MaxChars) break;
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

        // Elementos tras los cuales va un espacio (párrafos, celdas, filas…)
        private static readonly HashSet<string> Breaks = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "br", "tab", "tc", "tr", "si", "c", "row", "h", "list-item", "table-cell", "line-break", "s", "div", "li", "td"
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
                                if (Breaks.Contains(reader.LocalName)) sb.Append(' ');
                                break;
                            case XmlNodeType.Element:
                                if (reader.IsEmptyElement && Breaks.Contains(reader.LocalName)) sb.Append(' ');
                                break;
                        }
                        if (sb.Length > MaxChars) return sb.ToString();
                    }
                }
                catch (XmlException) { /* parte dañada: seguimos con la siguiente */ }
                sb.Append(' ');
            }
            return sb.ToString();
        }
    }
}
