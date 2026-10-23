using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland
{
    /// <summary>Espacio "Zonas": disposición de ventanas estilo FancyZones para la pantalla de esta isla.</summary>
    public partial class ZonesSpace : UserControl
    {
        private static ZoneService Z => IslandTools.Zones;

        private string _screenId = "";
        private bool _loading;

        private static readonly Brush TileZone = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        private static readonly Brush TileZoneOn = Frozen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly Brush PreviewZone = Frozen(Color.FromArgb(0x59, 0x0A, 0x84, 0xFF));
        private static readonly Brush PreviewZoneHover = Frozen(Color.FromArgb(0xB3, 0x0A, 0x84, 0xFF));
        private static readonly Brush PreviewStroke = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

        public ZonesSpace()
        {
            InitializeComponent();
            Z.Changed += () => Dispatcher.BeginInvoke(new Action(Refresh));
            Loaded += (_, _) => Refresh();
        }

        /// <summary>Monitor al que pertenece la isla que contiene este espacio.</summary>
        public string ScreenId
        {
            get => _screenId;
            set { _screenId = value; if (IsLoaded) Refresh(); }
        }

        private void Refresh()
        {
            if (_screenId.Length == 0) return;
            _loading = true;
            var layout = Z.LayoutFor(_screenId);
            var (cols, rows) = ZoneLayouts.GridSize(layout.Key);
            ColsText.Text = cols.ToString();
            RowsText.Text = rows.ToString();
            SpacingSlider.Value = Z.Spacing;
            SpacingText.Text = $"{Z.Spacing} px";
            ShiftButton.Content = !Z.Enabled ? "Apagadas" : Z.RequireShift ? "Con Shift" : "Siempre";
            HintText.Text = (!Z.Enabled
                    ? "Las zonas están apagadas. "
                    : Z.RequireShift
                        ? "Arrastra una ventana con Shift y suéltala en una zona. "
                        : "Arrastra una ventana y suéltala en una zona (Shift las oculta). ")
                + "Con Ctrl abarca varias zonas. "
                + (Z.HotkeyLabel.Length > 0 ? Z.HotkeyLabel + " mueve la ventana activa entre zonas." : "");
            BuildTiles(layout.Key);
            BuildPreview(layout);
            _loading = false;
        }

        // ---------- Lista de disposiciones ----------

        private void BuildTiles(string selectedKey)
        {
            LayoutTiles.Children.Clear();
            var all = new List<ZoneLayout>(ZoneLayouts.Presets);
            var grid = ZoneLayouts.Get(selectedKey);
            if (grid.Key.StartsWith("grid:")) all.Add(grid);
            else { var (c, r) = ZoneLayouts.GridSize(null); all.Add(ZoneLayouts.Grid(c, r)); }
            foreach (var c in ZoneLayouts.Saved) all.Add(c.ToLayout());

            foreach (var l in all)
            {
                bool on = l.Key == selectedKey;
                var canvas = new Canvas { Width = 44, Height = 26 };
                foreach (var z in l.Zones)
                {
                    var box = new Border
                    {
                        Width = Math.Max(2, z.Width * 44 - 2),
                        Height = Math.Max(2, z.Height * 26 - 2),
                        CornerRadius = new CornerRadius(2),
                        Background = on ? TileZoneOn : TileZone
                    };
                    Canvas.SetLeft(box, z.X * 44 + 1);
                    Canvas.SetTop(box, z.Y * 26 + 1);
                    canvas.Children.Add(box);
                }
                var tile = new Border
                {
                    Width = 58,
                    Padding = new Thickness(0, 5, 0, 4),
                    Margin = new Thickness(0, 0, 4, 4),
                    CornerRadius = new CornerRadius(12),
                    Background = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Surface"),
                    Cursor = Cursors.Hand,
                    Tag = l.Key,
                    ToolTip = l.Name,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            canvas,
                            new TextBlock
                            {
                                Text = l.Name, FontSize = 9.5, Margin = new Thickness(3, 3, 3, 0),
                                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                Foreground = on ? Brushes.White : (Brush)FindResource("TextSecondary")
                            }
                        }
                    }
                };
                tile.MouseLeftButtonUp += (s, _) => Z.SetLayout(_screenId, (string)((FrameworkElement)s).Tag);
                tile.MouseEnter += (s, _) => { if (((Border)s).Tag as string != selectedKey) ((Border)s).Background = (Brush)FindResource("ItemHover"); };
                tile.MouseLeave += (s, _) => { if (((Border)s).Tag as string != selectedKey) ((Border)s).Background = (Brush)FindResource("Surface"); };

                // Los diseños propios se pueden editar o borrar con el botón derecho
                if (l.Key.StartsWith("custom:"))
                {
                    string id = l.Key[7..];
                    var menu = new ContextMenu();
                    var edit = new MenuItem { Header = "Editar…" };
                    edit.Click += (_, _) => Z.OpenEditor(_screenId, "custom:" + id);
                    var del = new MenuItem { Header = "Eliminar" };
                    del.Click += (_, _) => Z.DeleteCustom(id);
                    menu.Items.Add(edit);
                    menu.Items.Add(del);
                    tile.ContextMenu = menu;
                    tile.ToolTip = l.Name + " — clic derecho para editar o eliminar";
                }

                LayoutTiles.Children.Add(tile);
            }

            LayoutTiles.Children.Add(EditorTile());
        }

        /// <summary>Mosaico final: abre el editor para dibujar las zonas a mano.</summary>
        private Border EditorTile()
        {
            var canvas = new Canvas { Width = 44, Height = 26 };
            foreach (var r in new[] { new Rect(0, 0, 0.56, 1), new Rect(0.6, 0, 0.4, 0.55), new Rect(0.44, 0.62, 0.56, 0.38) })
            {
                var box = new Border
                {
                    Width = Math.Max(2, r.Width * 44 - 2),
                    Height = Math.Max(2, r.Height * 26 - 2),
                    CornerRadius = new CornerRadius(2),
                    Background = TileZone,
                    BorderBrush = TileZoneOn,
                    BorderThickness = new Thickness(0.6)
                };
                Canvas.SetLeft(box, r.X * 44 + 1);
                Canvas.SetTop(box, r.Y * 26 + 1);
                canvas.Children.Add(box);
            }

            var tile = new Border
            {
                Width = 58,
                Padding = new Thickness(0, 5, 0, 4),
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("Surface"),
                Cursor = Cursors.Hand,
                ToolTip = "Dibujar las zonas a mano (se pueden superponer)",
                Child = new StackPanel
                {
                    Children =
                    {
                        canvas,
                        new TextBlock
                        {
                            Text = "Personalizar", FontSize = 9.5, Margin = new Thickness(3, 3, 3, 0),
                            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                            Foreground = (Brush)FindResource("TextSecondary")
                        }
                    }
                }
            };
            tile.MouseLeftButtonUp += (_, _) => Z.OpenEditor(_screenId);
            tile.MouseEnter += (s, _) => ((Border)s).Background = (Brush)FindResource("ItemHover");
            tile.MouseLeave += (s, _) => ((Border)s).Background = (Brush)FindResource("Surface");
            return tile;
        }

        private void Grid_Click(object sender, RoutedEventArgs e)
        {
            var (cols, rows) = ZoneLayouts.GridSize(Z.LayoutFor(_screenId).Key);
            switch ((sender as FrameworkElement)?.Tag as string)
            {
                case "c-": cols--; break;
                case "c+": cols++; break;
                case "r-": rows--; break;
                case "r+": rows++; break;
            }
            Z.SetLayout(_screenId, ZoneLayouts.Grid(cols, rows).Key);
        }

        private void Spacing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || !IsLoaded) return; // el valor inicial del XAML no debe pisar lo guardado
            int v = (int)Math.Round(e.NewValue);
            SpacingText.Text = $"{v} px";
            if (v != Z.Spacing) Z.SetSpacing(v);
        }

        /// <summary>Con Shift → Siempre → Apagadas → Con Shift…</summary>
        private void Shift_Click(object sender, RoutedEventArgs e)
        {
            if (!Z.Enabled) { Z.RequireShift = true; Z.Enabled = true; }
            else if (Z.RequireShift) Z.RequireShift = false;
            else Z.Enabled = false;

            AppSettings.Current.ZonesEnabled = Z.Enabled;
            AppSettings.Current.ZonesRequireShift = Z.RequireShift;
            AppSettings.Current.Save();
            Refresh();
        }

        // ---------- Vista previa con clic ----------

        private ZoneLayout? _previewLayout;

        private void Preview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_previewLayout != null) BuildPreview(_previewLayout);
        }

        private void BuildPreview(ZoneLayout layout)
        {
            _previewLayout = layout;
            Preview.Children.Clear();
            double w = Preview.ActualWidth, h = Preview.ActualHeight;
            if (w < 10 || h < 10) return;

            // Mantener la proporción del monitor
            var screen = ZoneService.ScreenById(_screenId);
            double aspect = screen != null ? (double)screen.WorkingArea.Width / Math.Max(1, screen.WorkingArea.Height) : 16.0 / 9;
            double pw = w, ph = w / aspect;
            if (ph > h) { ph = h; pw = h * aspect; }
            double ox = (w - pw) / 2, oy = (h - ph) / 2;

            var frame = new Border
            {
                Width = pw, Height = ph, CornerRadius = new CornerRadius(6),
                BorderBrush = PreviewStroke, BorderThickness = new Thickness(1), Background = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
            };
            Canvas.SetLeft(frame, ox); Canvas.SetTop(frame, oy);
            Preview.Children.Add(frame);

            double gap = Math.Max(2, Z.Spacing * pw / Math.Max(1, screen?.WorkingArea.Width ?? 1920));
            for (int i = 0; i < layout.Zones.Count; i++)
            {
                var z = layout.Zones[i];
                var box = new Border
                {
                    Width = Math.Max(4, z.Width * pw - gap),
                    Height = Math.Max(4, z.Height * ph - gap),
                    CornerRadius = new CornerRadius(4),
                    Background = PreviewZone,
                    Cursor = Cursors.Hand,
                    Tag = i,
                    ToolTip = $"Enviar a la zona {i + 1}",
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                box.MouseEnter += (s, _) => ((Border)s).Background = PreviewZoneHover;
                box.MouseLeave += (s, _) => ((Border)s).Background = PreviewZone;
                box.MouseLeftButtonUp += (s, _) => Z.SnapForegroundToZone((int)((FrameworkElement)s).Tag, _screenId);
                Canvas.SetLeft(box, ox + z.X * pw + gap / 2);
                Canvas.SetTop(box, oy + z.Y * ph + gap / 2);
                Preview.Children.Add(box);
            }
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
