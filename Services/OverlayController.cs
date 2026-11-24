using System.Windows;
using SpotOnFly.Models;
using SpotOnFly.Views;

namespace SpotOnFly.Services;

public sealed class OverlayController : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly MediaWatcher _mediaWatcher;
    private readonly AlbumArtService _albumArtService;
    private readonly OverlayWindow _window;
    private TrackInfo? _currentTrack;
    private CancellationTokenSource? _artCancellation;

    public OverlayController(
        SettingsService settingsService,
        MediaWatcher mediaWatcher,
        AlbumArtService albumArtService)
    {
        _settingsService = settingsService;
        _mediaWatcher = mediaWatcher;
        _albumArtService = albumArtService;
        _window = new OverlayWindow(settingsService);

        _window.PreviousRequested += (_, _) => _ = _mediaWatcher.SkipPreviousAsync();
        _window.PlayPauseRequested += (_, _) => _ = _mediaWatcher.TogglePlayPauseAsync();
        _window.NextRequested += (_, _) => _ = _mediaWatcher.SkipNextAsync();

        _mediaWatcher.PlaybackStateChanged += OnPlaybackStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void HandleTrackChanged(TrackInfo? track)
    {
        // Mémorisé même lorsque l'overlay est désactivé, afin de pouvoir
        // réafficher la piste en cours au moment où l'utilisateur le réactive.
        // La valeur null efface la mémoire lorsque la session média disparaît.
        Volatile.Write(ref _currentTrack, track);

        if (OverlayDisplayPolicy.ShouldShowTrack(track, _settingsService.Settings.Enabled))
        {
            RunOnUi(() => ShowTrack(track!));
        }
    }

    private void ShowTrack(TrackInfo track)
    {
        // Une nouvelle piste annule la recherche de pochette en cours.
        CancelAlbumArtLookup();

        _window.ShowTrack(track);

        if (OverlayDisplayPolicy.ShouldLookUpAlbumArt(track))
        {
            StartAlbumArtLookup(track);
        }
    }

    private void StartAlbumArtLookup(TrackInfo track)
    {
        var cancellation = new CancellationTokenSource();
        _artCancellation = cancellation;
        _ = LoadAlbumArtAsync(track, cancellation);
    }

    private void CancelAlbumArtLookup()
    {
        var cancellation = _artCancellation;
        _artCancellation = null;
        cancellation?.Cancel();
    }

    private async Task LoadAlbumArtAsync(TrackInfo track, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;

        try
        {
            var artwork = await _albumArtService
                .GetAlbumArtAsync(track, token)
                .ConfigureAwait(false);

            if (artwork is null || token.IsCancellationRequested)
            {
                return;
            }

            RunOnUi(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(_window.CurrentTrack, track))
                {
                    _window.UpdateAlbumArt(artwork);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // La piste a changé : la pochette n'est plus pertinente.
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
        finally
        {
            RunOnUi(() =>
            {
                if (ReferenceEquals(_artCancellation, cancellation))
                {
                    _artCancellation = null;
                }

                cancellation.Dispose();
            });
        }
    }

    public void SetEnabled(bool enabled)
    {
        _settingsService.Update(settings => settings.Enabled = enabled);

        if (OverlayDisplayPolicy.ShouldCancelAlbumArtLookup(enabled))
        {
            CancelAlbumArtLookup();
            RunOnUi(_window.HideOverlay);
            return;
        }

        var track = Volatile.Read(ref _currentTrack);
        if (OverlayDisplayPolicy.ShouldRestoreTrack(track, enabled))
        {
            RunOnUi(() => ShowTrack(track!));
        }
    }

    public void SetPosition(OverlayPosition position)
        => _settingsService.Update(settings => settings.Position = position);

    public void SetSize(OverlaySize size)
        => _settingsService.Update(settings => settings.Size = size);

    public void SetHoldSeconds(double seconds)
        => _settingsService.Update(settings => settings.HoldSeconds = seconds);

    public void ShowPreview()
    {
        RunOnUi(() =>
        {
            AppLog.Info("Overlay de démonstration demandé.");
            var preview = new TrackInfo(
                "Titre de démonstration",
                "SpotOnFly",
                null,
                "preview",
                null);
            _window.ShowTrack(preview);
        });
    }

    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
        => RunOnUi(() => _window.UpdatePlaybackState(isPlaying));

    private void OnSettingsChanged(object? sender, EventArgs e)
        => RunOnUi(_window.ApplySettings);

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.InvokeAsync(action);
        }
    }

    public void Dispose()
    {
        CancelAlbumArtLookup();
        _mediaWatcher.PlaybackStateChanged -= OnPlaybackStateChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _window.Close();
    }
}
