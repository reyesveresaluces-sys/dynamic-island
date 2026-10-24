using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>Espacio "Ventanas": lo que tienes abierto, con su captura, y lo guardado en la isla.</summary>
    public partial class WindowsSpace : UserControl
    {
        private static WindowService W => IslandTools.Windows;

        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _active;

        private static readonly Brush Line = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        private static readonly Brush StashedStroke = Frozen(Color.FromArgb(0xCC, 0x0A, 0x84, 0xFF));

        public WindowsSpace()
        {
            InitializeComponent();
            _refresh.Tick += (_, _) => Reload();
            W.Changed += OnChanged;
            Unloaded += (_, _) => _refresh.Stop();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(new Action(() => { if (_active) Reload(); }));

        /// <summary>La isla avisa cuándo está a la vista: solo entonces se capturan las ventanas.</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (active) { Reload(); _refresh.Start(); }
            else _refresh.Stop();
        }

        private void Reload()
        {
            List<OpenWindow> all;
            try { all = W.List(); }
            catch { return; }

            Cards.Children.Clear();
            foreach (var w in all) Cards.Children.Add(Card(w));

            EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int stashed = 0;
            foreach (var w in all) if (w.Stashed || w.Minimized) stashed++;
            string hotkey = W.HotkeyLabel.Length > 0 ? $"  ·  {W.HotkeyLabel}" : "";
            CountText.Text = all.Count == 0 ? "" : $"{all.Count} ventanas · {stashed} minimizadas{hotkey}";
        }

        private Border Card(OpenWindow w)
        {
            // Miniatura: la captura, o un bloque de color con la inicial de la app
            var thumb = new Border
            {
                Width = 130,
                Height = 74,
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = w.Shot is null ? w.AccentBrush : Line,
                BorderBrush = w.Stashed ? StashedStroke : Line,
                BorderThickness = new Thickness(w.Stashed ? 1.6 : 1)
            };

            if (w.Shot is not null)
            {
                thumb.Child = new Image
                {
                    Source = w.Shot,
                    Stretch = Stretch.UniformToFill,
                    StretchDirection = StretchDirection.Both,
                    VerticalAlignment = VerticalAlignment.Top
                };
            }
            else
            {
                thumb.Child = new TextBlock
                {
                    Text = w.AppName.Length > 0 ? w.AppName[..1].ToUpperInvariant() : "?",
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    Opacity = 0.9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var title = new TextBlock
            {
                Text = w.Headline,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(1, 6, 1, 0)
            };

            var app = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 2, 1, 0) };
            if (w.Icon is not null)
                app.Children.Add(new Image { Source = w.Icon, Width = 12, Height = 12, Margin = new Thickness(0, 0, 5, 0) });
            app.Children.Add(new TextBlock
            {
                Text = w.Minimized ? w.AppName + " · guardada" : w.AppName,
                FontSize = 9.5,
                Foreground = (Brush)FindResource(w.Minimized ? "TextTertiary" : "TextSecondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var card = new Border
            {
                Width = 146,
                Padding = new Thickness(7, 7, 7, 8),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("Surface"),
                Cursor = Cursors.Hand,
                Opacity = w.CardOpacity,
                ToolTip = w.Headline + "\n" + w.AppName,
                Child = new StackPanel { Children = { thumb, title, app } }
            };

            card.MouseLeftButtonUp += (_, _) => Restore(w);
            card.MouseEnter += (s, _) => ((Border)s).Background = (Brush)FindResource("ItemHover");
            card.MouseLeave += (s, _) => ((Border)s).Background = (Brush)FindResource("Surface");

            var menu = new ContextMenu();
            if (!w.Minimized)
            {
                var stash = new MenuItem { Header = "Guardar en la isla" };
                stash.Click += (_, _) => { W.Stash(w.Handle); Reload(); };
                menu.Items.Add(stash);
            }
            var front = new MenuItem { Header = "Traer al frente" };
            front.Click += (_, _) => Restore(w);
            menu.Items.Add(front);
            card.ContextMenu = menu;

            return card;
        }

        private void Restore(OpenWindow w)
        {
            W.Activate(w.Handle);
            Reload();
        }

        private void Stash_Click(object sender, RoutedEventArgs e)
        {
            W.StashForeground();
            Reload();
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
