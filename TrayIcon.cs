using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DynamicIsland
{
    /// <summary>Dibuja el ícono de la bandeja (una píldora negra) sin necesidad de archivos .ico.</summary>
    internal static class TrayIcon
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = RoundedRect(new RectangleF(2, 10, 28, 12), 6);
                using var fill = new SolidBrush(Color.FromArgb(255, 18, 18, 20));
                using var border = new Pen(Color.FromArgb(255, 120, 120, 128), 1.5f);
                g.FillPath(fill, path);
                g.DrawPath(border, path);
                using var dot = new SolidBrush(Color.FromArgb(255, 48, 209, 88));
                g.FillEllipse(dot, 21, 13.5f, 5, 5);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
