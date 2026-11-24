using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SpotOnFly.Models;
using SpotOnFly.Services;

namespace SpotOnFly;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private SettingsService? _settingsService;
    private MediaWatcher? _mediaWatcher;
    private AlbumArtService? _albumArtService;
    private OverlayController? _overlayController;
    private TrayService? _trayService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\SpotOnFly.SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "SpotOnFly est déjà en cours d'exécution. Utilisez l'icône dans la zone de notification.",
                "SpotOnFly",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error(args.Exception);
            args.SetObserved();
        };

        try
        {
            _settingsService = new SettingsService();
            _settingsService.Load();

            if (_settingsService.Settings.RunAtStartup)
            {
                // Rafraîchit la commande au cas où l'exécutable aurait été déplacé.
                AutoStartService.SetEnabled(true);
            }

            _mediaWatcher = new MediaWatcher(_settingsService.Settings);
            _mediaWatcher.TrackChanged += OnTrackChanged;

            _albumArtService = new AlbumArtService();
            _overlayController = new OverlayController(_settingsService, _mediaWatcher, _albumArtService);
            _trayService = new TrayService(_settingsService, _overlayController);
            _trayService.ExitRequested += (_, _) => Shutdown();
            _trayService.Initialize();

            _ = InitializeMediaAsync();

            if (e.Args.Any(arg => string.Equals(arg, "--preview", StringComparison.OrdinalIgnoreCase)))
            {
                _overlayController.ShowPreview();
            }

            AppLog.Info("SpotOnFly démarré.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            MessageBox.Show(
                "SpotOnFly n'a pas pu démarrer :" + Environment.NewLine + ex.Message,
                "SpotOnFly",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnTrackChanged(object? sender, TrackInfo? track)
    {
        _overlayController?.HandleTrackChanged(track);
        _trayService?.UpdateNowPlaying(track);
    }

    private async Task InitializeMediaAsync()
    {
        try
        {
            await _mediaWatcher!.InitializeAsync();
            AppLog.Info("Session media Windows (GSMTC) initialisee.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            _trayService?.SetError("Sessions média Windows indisponibles");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error(e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mediaWatcher?.Dispose();
        _trayService?.Dispose();
        _overlayController?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
