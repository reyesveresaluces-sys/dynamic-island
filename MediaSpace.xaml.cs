using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>Espacio "Música": portada, barra de avance, controles y volumen general.</summary>
    public partial class MediaSpace : UserControl
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private bool _seeking, _updatingVolume;

        public MediaSpace()
        {
            InitializeComponent();
            _timer.Tick += (_, _) => Refresh();
        }

        /// <summary>Se actualiza solo mientras el espacio está visible.</summary>
        public void SetActive(bool active)
        {
            if (active) { Refresh(); _timer.Start(); }
            else _timer.Stop();
        }

        private void Refresh()
        {
            var m = IslandTools.Media;
            bool has = m?.HasSession == true;

            ArtBrush.ImageSource = has ? m!.Art : null;
            TitleText.Text = has ? m!.Title : "Nada en reproducción";
            ArtistText.Text = has ? m!.Artist : "Reproduce algo en Spotify, YouTube o tu reproductor";
            AppText.Text = has ? m!.AppName : "";
            PlayGlyph.Text = has && m!.IsPlaying ? "" : "";

            bool timeline = has && m!.Duration > TimeSpan.Zero;
            SeekRow.Visibility = timeline ? Visibility.Visible : Visibility.Hidden;
            SeekSlider.IsEnabled = timeline && m!.CanSeek;
            if (timeline && !_seeking)
            {
                double dur = m!.Duration.TotalSeconds;
                SeekSlider.Maximum = Math.Max(1, dur);
                SeekSlider.Value = Math.Min(dur, m.Position.TotalSeconds);
                PosText.Text = Format(m.Position);
                DurText.Text = Format(m.Duration);
            }

            var vol = IslandTools.Volume.Read();
            if (vol != null)
            {
                _updatingVolume = true;
                VolumeSlider.Value = Math.Round(vol.Value.level * 100);
                _updatingVolume = false;
                UpdateVolumeGlyph(vol.Value.level, vol.Value.muted);
            }
        }

        private void UpdateVolumeGlyph(double level, bool muted)
        {
            VolumeGlyph.Text = muted ? "" : level <= 0.01 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
            VolumeText.Text = muted ? "Mudo" : $"{Math.Round(level * 100)}%";
        }

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        // ---------- Avance ----------
        private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

        private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // Esperar a que el control mueva el valor al punto del clic
            Dispatcher.BeginInvoke(new Action(() =>
            {
                IslandTools.Media?.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
                _seeking = false;
            }), DispatcherPriority.Input);
        }

        // ---------- Controles ----------
        private void Prev_Click(object sender, RoutedEventArgs e) => IslandTools.Media?.Previous();
        private void Next_Click(object sender, RoutedEventArgs e) => IslandTools.Media?.Next();
        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            IslandTools.Media?.TogglePlayPause();
            Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
        }

        // ---------- Volumen ----------
        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolume || !IsLoaded) return;
            IslandTools.Volume.SetLevel(e.NewValue / 100.0);
            UpdateVolumeGlyph(e.NewValue / 100.0, false);
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
            e.Handled = true; // no cambiar de espacio
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            IslandTools.Volume.ToggleMute();
            Refresh();
        }
    }
}
