using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotOnFly.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotOnFly.Services;

/// <summary>
/// Observe les sessions média exposées par Windows (GSMTC) et signale
/// chaque changement de piste effectivement en lecture.
/// </summary>
public sealed class MediaWatcher : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _lastSignature;
    private bool _disposed;

    public event EventHandler<TrackInfo?>? TrackChanged;

    public event EventHandler<bool>? PlaybackStateChanged;

    public MediaWatcher(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        AttachSession(_manager.GetCurrentSession());
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        AttachSession(sender.GetCurrentSession());
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;

        if (session is null)
        {
            _lastSignature = null;
            TrackChanged?.Invoke(this, null);
            return;
        }

        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _ = PublishAsync(session, requirePlaying: true);
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
        => _ = PublishAsync(sender, requirePlaying: false);

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        var playback = sender.GetPlaybackInfo();
        var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlaybackStateChanged?.Invoke(this, isPlaying);

        // Une lecture qui (re)démarre peut précéder un changement de métadonnées.
        if (isPlaying)
        {
            _ = PublishAsync(sender, requirePlaying: false);
        }
    }

    private async Task PublishAsync(GlobalSystemMediaTransportControlsSession session, bool requirePlaying)
    {
        if (_disposed)
        {
            return;
        }

        await _updateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(session, _session))
            {
                return;
            }

            var playback = session.GetPlaybackInfo();
            if (requirePlaying && playback?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null)
            {
                return;
            }

            var title = properties.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var appId = session.SourceAppUserModelId ?? string.Empty;
            if (IsIgnored(appId, _settings.IgnoredApps))
            {
                return;
            }

            var signature = BuildTrackSignature(appId, title, properties.Artist);
            if (signature == _lastSignature)
            {
                return;
            }

            var thumbnail = await TryLoadThumbnailAsync(properties.Thumbnail);
            if (_disposed || !ReferenceEquals(session, _session))
            {
                return;
            }

            if (properties.Thumbnail is null)
            {
                AppLog.Info($"Pochette GSMTC absente : « {title} » — {properties.Artist}.");
            }

            _lastSignature = signature;
            TrackChanged?.Invoke(this, new TrackInfo(title, properties.Artist, properties.AlbumTitle, appId, thumbnail)
            {
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanSkipPrevious = playback?.Controls?.IsPreviousEnabled == true,
                CanPlayPause = playback?.Controls?.IsPlayPauseToggleEnabled == true,
                CanSkipNext = playback?.Controls?.IsNextEnabled == true
            });
            AppLog.Info($"Piste détectée : {title} — {properties.Artist} ({appId})");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    internal static bool IsIgnored(string appId, IEnumerable<string>? ignoredApps)
    {
        if (ignoredApps is null)
        {
            return false;
        }

        foreach (var ignored in ignoredApps)
        {
            if (!string.IsNullOrWhiteSpace(ignored) &&
                appId.Contains(ignored, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Signature utilisée pour éviter de republier deux fois la même piste.
    /// Les champs sont séparés par un caractère de contrôle absent des métadonnées.
    /// </summary>
    internal static string BuildTrackSignature(string appId, string title, string? artist)
        => $"{appId}\u001f{title}\u001f{artist}";

    private static async Task<ImageSource?> TryLoadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0)
            {
                AppLog.Info("Pochette GSMTC vide.");
                return null;
            }

            if (stream.Size > 8 * 1024 * 1024)
            {
                AppLog.Info($"Pochette GSMTC ignorée : {stream.Size} octets.");
                return null;
            }

            var buffer = new byte[(int)stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)buffer.Length);
            reader.ReadBytes(buffer);

            using var memory = new MemoryStream(buffer);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Échec du chargement de la pochette GSMTC : {ex.Message}");
            return null;
        }
    }

    public async Task SkipPreviousAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var result = await session.TrySkipPreviousAsync();
            AppLog.Info($"Commande média : précédent (résultat={result}).");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    public async Task SkipNextAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var result = await session.TrySkipNextAsync();
            AppLog.Info($"Commande média : suivant (résultat={result}).");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var isPlaying = session.GetPlaybackInfo()?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (isPlaying)
            {
                var result = await session.TryPauseAsync();
                AppLog.Info($"Commande média : pause (résultat={result}).");
            }
            else
            {
                var result = await session.TryPlayAsync();
                AppLog.Info($"Commande média : lecture (résultat={result}).");
            }

            PlaybackStateChanged?.Invoke(this, !isPlaying);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        // Ne pas disposer _updateGate : une publication déjà en vol pourrait
        // appeler Release() après la fermeture et lever ObjectDisposedException.
        // SemaphoreSlim n'exige pas d'être disposé lorsqu'il n'utilise pas
        // AvailableWaitHandle.
    }
}
