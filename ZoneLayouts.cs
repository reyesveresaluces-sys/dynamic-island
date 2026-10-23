using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace DynamicIsland
{
    /// <summary>Una disposición de zonas (estilo FancyZones). Las zonas van en fracciones 0–1 del área de trabajo.</summary>
    public sealed class ZoneLayout
    {
        public string Key { get; }
        public string Name { get; }
        public IReadOnlyList<Rect> Zones { get; }

        public ZoneLayout(string key, string name, IReadOnlyList<Rect> zones)
        {
            Key = key; Name = name; Zones = zones;
        }

        /// <summary>Zonas en píxeles para un área de trabajo, con separación entre ellas.</summary>
        public List<Rect> Resolve(Rect area, double spacing)
        {
            var result = new List<Rect>(Zones.Count);
            double half = spacing / 2;
            var inner = new Rect(area.X + half, area.Y + half, Math.Max(1, area.Width - spacing), Math.Max(1, area.Height - spacing));
            foreach (var z in Zones)
            {
                double x = inner.X + z.X * inner.Width, y = inner.Y + z.Y * inner.Height;
                double w = z.Width * inner.Width, h = z.Height * inner.Height;
                // La separación se reparte entre zonas vecinas; nunca se encoge por debajo de 40 px
                result.Add(new Rect(x + half, y + half, Math.Max(40, w - spacing), Math.Max(40, h - spacing)));
            }
            return result;
        }
    }

    /// <summary>Una zona guardada, en fracciones 0–1 del área de trabajo (apta para JSON).</summary>
    public sealed class ZoneBox
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }

        public Rect ToRect() => new(X, Y, Math.Max(0.01, W), Math.Max(0.01, H));
    }

    /// <summary>Un diseño hecho a mano en el editor: zonas libres, que pueden superponerse.</summary>
    public sealed class CustomZoneLayout
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "Mi diseño";
        public List<ZoneBox> Zones { get; set; } = new();

        public string Key => "custom:" + Id;

        public ZoneLayout ToLayout() =>
            new(Key, Name, Zones.ConvertAll(z => z.ToRect()));
    }

    /// <summary>Disposiciones predefinidas, cuadrículas ("grid:3x2") y diseños propios ("custom:ab12cd34").</summary>
    public static class ZoneLayouts
    {
        public const string DefaultKey = "columns2";

        public static readonly IReadOnlyList<ZoneLayout> Presets = new[]
        {
            new ZoneLayout("focus", "Enfoque", new[] { new Rect(0.12, 0.06, 0.76, 0.88) }),
            new ZoneLayout("columns2", "Columnas", new[] { new Rect(0, 0, 0.5, 1), new Rect(0.5, 0, 0.5, 1) }),
            new ZoneLayout("columns3", "Tres columnas", new[] { new Rect(0, 0, 1.0 / 3, 1), new Rect(1.0 / 3, 0, 1.0 / 3, 1), new Rect(2.0 / 3, 0, 1.0 / 3, 1) }),
            new ZoneLayout("rows2", "Filas", new[] { new Rect(0, 0, 1, 0.5), new Rect(0, 0.5, 1, 0.5) }),
            new ZoneLayout("grid2x2", "Cuadrícula", new[] { new Rect(0, 0, 0.5, 0.5), new Rect(0.5, 0, 0.5, 0.5), new Rect(0, 0.5, 0.5, 0.5), new Rect(0.5, 0.5, 0.5, 0.5) }),
            new ZoneLayout("priority", "Prioridad", new[] { new Rect(0, 0, 0.6, 1), new Rect(0.6, 0, 0.4, 0.5), new Rect(0.6, 0.5, 0.4, 0.5) }),
            new ZoneLayout("priority3", "Prioridad central", new[] { new Rect(0, 0, 0.25, 1), new Rect(0.25, 0, 0.5, 1), new Rect(0.75, 0, 0.25, 1) }),
            new ZoneLayout("wide", "Ancha + lateral", new[] { new Rect(0, 0, 0.7, 1), new Rect(0.7, 0, 0.3, 1) })
        };

        /// <summary>Los diseños hechos en el editor, en el orden guardado.</summary>
        public static List<CustomZoneLayout> Saved => AppSettings.Current.CustomLayouts;

        /// <summary>Devuelve la disposición de una clave; "grid:CxR" crea una cuadrícula y "custom:id" busca un diseño propio.</summary>
        public static ZoneLayout Get(string? key)
        {
            if (string.IsNullOrEmpty(key)) key = DefaultKey;
            foreach (var p in Presets) if (p.Key == key) return p;

            if (key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                string id = key[7..];
                foreach (var c in Saved)
                    if (c.Id == id && c.Zones.Count > 0) return c.ToLayout();
            }

            if (key.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = key[5..].Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cols)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows))
                    return Grid(cols, rows);
            }
            return Presets[1];
        }

        public static ZoneLayout Grid(int cols, int rows)
        {
            cols = Math.Clamp(cols, 1, 6); rows = Math.Clamp(rows, 1, 4);
            var zones = new List<Rect>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    zones.Add(new Rect((double)c / cols, (double)r / rows, 1.0 / cols, 1.0 / rows));
            return new ZoneLayout($"grid:{cols}x{rows}", $"Cuadrícula {cols}×{rows}", zones);
        }

        public static (int cols, int rows) GridSize(string? key)
        {
            var l = Get(key);
            if (!l.Key.StartsWith("grid:")) return (3, 2);
            var p = l.Key[5..].Split('x');
            return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
        }
    }
}
