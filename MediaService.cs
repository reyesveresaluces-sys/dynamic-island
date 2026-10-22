using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland
{
    /// <summary>
    /// Lee y controla la música que suena en Windows (Spotify, navegador, Apple Music, VLC…)
    /// mediante GlobalSystemMediaTransportControls.
    /// </summary>
    public sealed class MediaService
    {
        public event Action? Changed;       // cualquier cambio (estado, canción, portada)
        public event Action? TrackChanged;  // empezó una canción distinta

        public bool HasSession { get; private set; }
        public bool IsPlaying { get; private set; }
        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public string AppName { get; private set; } = "";
        public string AppId { get; private set; } = "";
        public ImageSource? Art { get; private set; }

        private readonly Dispatcher _ui;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private string? _lastKey;
        private bool _firstLoad = true;

        public MediaService(Dispatcher ui) => _ui = ui;

        public async Task StartAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                Attach(_manager.GetCurrentSession());
            }
            catch
            {
                // API no disponible: la isla funciona sin música
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
            => _ui.InvokeAsync(() => Attach(sender.GetCurrentSession()));

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => _ui.InvokeAsync(RefreshAsync);

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => _ui.InvokeAsync(RefreshAsync);

        private void Attach(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }

            _session = session;

            if (_session == null)
            {
                HasSession = false;
                IsPlaying = false;
                Changed?.Invoke();
                return;
            }

            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            AppId = _session.SourceAppUserModelId ?? "";
            AppName = NotificationService.PrettyAppName(AppId);
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var playback = s.GetPlaybackInfo();
                if (s != _session) return; // cambió la sesión mientras esperábamos

                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string title = props?.Title ?? "";
                string artist = props?.Artist ?? "";
                if (string.IsNullOrWhiteSpace(artist)) artist = props?.AlbumArtist ?? "";

                string key = title + "|" + artist;
                bool newTrack = key != _lastKey;
                if (newTrack || Art == null)
                    Art = await LoadThumbnailAsync(props?.Thumbnail);

                Title = title;
                Artist = artist;
                HasSession = !string.IsNullOrWhiteSpace(title);
                Changed?.Invoke();

                if (newTrack && !_firstLoad && HasSession && IsPlaying) TrackChanged?.Invoke();
                if (HasSession) _firstLoad = false;
                _lastKey = key;
            }
            catch
            {
                // algunos reproductores fallan momentáneamente al cambiar de pista
            }
        }

        private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? thumb)
        {
            if (thumb == null) return null;
            try
            {
                using var ras = await thumb.OpenReadAsync();
                using var src = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                if (ms.Length == 0) return null;
                ms.Position = 0;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 160;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        public async void TogglePlayPause()
        {
            try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
        }

        public async void Next()
        {
            try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
        }

        public async void Previous()
        {
            try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
        }
    }
}
