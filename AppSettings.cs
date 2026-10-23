using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DynamicIsland
{
    /// <summary>Un elemento fijado del portapapeles que se conserva entre sesiones.</summary>
    public sealed class PinnedClip
    {
        public string Kind { get; set; } = "Text";
        public string Text { get; set; } = "";
        public string ImageFile { get; set; } = "";
        public List<string> Files { get; set; } = new();
        public DateTime Time { get; set; } = DateTime.Now;
    }

    /// <summary>Ajustes que se guardan entre sesiones (%LOCALAPPDATA%\DynamicIsland\settings.json).</summary>
    public sealed class AppSettings
    {
        public bool OcrEnabled { get; set; } = true;

        /// <summary>Borde donde se ancla la isla: Top, Left o Right.</summary>
        public string Dock { get; set; } = "Top";
        public bool MagicShake { get; set; } = true;
        public bool ClipboardHistory { get; set; } = true;
        public string LastSpace { get; set; } = "Notifications";
        public List<string> Launcher { get; set; } = new();
        public List<PinnedClip> PinnedClips { get; set; } = new();

        private static readonly string FilePath = Path.Combine(SearchIndex.DataDir, "settings.json");
        public static AppSettings Current { get; } = Load();

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { /* archivo dañado: valores por defecto */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SearchIndex.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
