using System;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland
{
    /// <summary>Una notificación lista para mostrarse en la isla.</summary>
    public sealed class IslandNotification
    {
        public uint Id { get; init; }
        public string AppName { get; init; } = "Aplicación";
        public string AppId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public DateTime Time { get; init; } = DateTime.Now;
        public ImageSource? Icon { get; init; }

        /// <summary>Sitio web que envió la notificación (p. ej. web.whatsapp.com) si vino de un navegador.</summary>
        public string Site { get; init; } = "";

        /// <summary>Orden de apertura original del toast (atributo launch); se pasa al activador de la app.</summary>
        public string LaunchArgs { get; init; } = "";

        /// <summary>Acción opcional al hacer clic (si no hay, se abre la app por su AppId).</summary>
        public Action? OnClick { get; init; }

        public string Initial => string.IsNullOrWhiteSpace(AppName) ? "?" : AppName.Trim()[0].ToString().ToUpperInvariant();
        public string TimeText => Time.ToString("HH:mm");
        public string Headline => string.IsNullOrWhiteSpace(Title) ? AppName : Title;
        public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility InitialVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BodyVisibility => string.IsNullOrWhiteSpace(Body) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Color del avatar cuando la app no expone ícono (estable por nombre).</summary>
        public Brush AccentBrush
        {
            get
            {
                string[] palette = { "#FF5E5CE6", "#FF0A84FF", "#FF30D158", "#FFFF9F0A", "#FFFF375F", "#FF64D2FF", "#FFBF5AF2" };
                int h = 0;
                foreach (char c in AppName) h = (h * 31 + c) & 0x7fffffff;
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[h % palette.Length]));
                b.Freeze();
                return b;
            }
        }
    }
}
