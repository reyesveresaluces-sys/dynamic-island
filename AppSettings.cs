using System;
using System.IO;
using System.Text.Json;

namespace DynamicIsland
{
    /// <summary>Ajustes que se guardan entre sesiones (%LOCALAPPDATA%\DynamicIsland\settings.json).</summary>
    public sealed class AppSettings
    {
        public bool OcrEnabled { get; set; } = true;

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
