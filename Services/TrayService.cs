using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using SpotOnFly.Models;

namespace SpotOnFly.Services;

public sealed class TrayService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly OverlayController _overlayController;
    private readonly Dictionary<OverlayPosition, MenuItem> _positionItems = new();
    private readonly Dictionary<OverlaySize, MenuItem> _sizeItems = new();
    private readonly Dictionary<int, MenuItem> _durationItems = new();
    private TaskbarIcon? _trayIcon;
    private MenuItem? _enabledItem;
    private MenuItem? _artistItem;
    private MenuItem? _startupItem;

    public event EventHandler? ExitRequested;

    public TrayService(SettingsService settingsService, OverlayController overlayController)
    {
        _settingsService = settingsService;
        _overlayController = overlayController;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "SpotOnFly",
            IconSource = LoadIconSource()
        };
        _trayIcon.ContextMenu = BuildContextMenu();
        _trayIcon.ForceCreate();
        AppLog.Info("Icône de notification créée.");
    }

    public void UpdateNowPlaying(TrackInfo? track)
    {
        if (track is null)
        {
            SetToolTip("SpotOnFly");
            return;
        }

        var artist = string.IsNullOrWhiteSpace(track.Artist) ? string.Empty : $" — {track.Artist}";
        SetToolTip($"SpotOnFly — {track.Title}{artist}");
    }

    public void SetError(string message) => SetToolTip($"SpotOnFly — {message}");

    private ContextMenu BuildContextMenu()
    {
        var settings = _settingsService.Settings;
        var menu = new ContextMenu();

        // Resynchronise les cases à chaque ouverture : les réglages ont pu être
        // modifiés depuis un autre point d'entrée pendant que le menu était fermé.
        menu.Opened += (_, _) => SyncMenuState();

        _enabledItem = new MenuItem
        {
            Header = "Afficher l'overlay",
            IsCheckable = true,
            IsChecked = settings.Enabled
        };
        _enabledItem.Click += (_, _) => _overlayController.SetEnabled(_enabledItem.IsChecked);
        menu.Items.Add(_enabledItem);

        _artistItem = new MenuItem
        {
            Header = "Afficher l'artiste",
            IsCheckable = true,
            IsChecked = settings.ShowArtist
        };
        _artistItem.Click += (_, _) => _settingsService.Update(s => s.ShowArtist = _artistItem.IsChecked);
        menu.Items.Add(_artistItem);

        var testItem = new MenuItem { Header = "Tester l'overlay" };
        testItem.Click += (_, _) => _overlayController.ShowPreview();
        menu.Items.Add(testItem);

        var positionMenu = new MenuItem { Header = "Position" };
        foreach (var position in Enum.GetValues<OverlayPosition>())
        {
            var item = new MenuItem
            {
                Header = PositionLabel(position),
                IsCheckable = true,
                IsChecked = settings.Position == position
            };
            item.Click += (_, _) => SelectPosition(position);
            _positionItems[position] = item;
            positionMenu.Items.Add(item);
        }

        menu.Items.Add(positionMenu);

        var sizeMenu = new MenuItem { Header = "Taille" };
        foreach (var size in Enum.GetValues<OverlaySize>())
        {
            var item = new MenuItem
            {
                Header = SizeLabel(size),
                IsCheckable = true,
                IsChecked = settings.Size == size
            };
            item.Click += (_, _) => SelectSize(size);
            _sizeItems[size] = item;
            sizeMenu.Items.Add(item);
        }

        menu.Items.Add(sizeMenu);

        var durationMenu = new MenuItem { Header = "Durée d'affichage" };
        for (var seconds = 1; seconds <= 5; seconds++)
        {
            var value = seconds;
            var item = new MenuItem
            {
                Header = value == 1 ? "1 seconde" : $"{value} secondes",
                IsCheckable = true,
                IsChecked = Math.Abs(settings.HoldSeconds - value) < 0.01
            };
            item.Click += (_, _) => SelectHoldSeconds(value);
            _durationItems[value] = item;
            durationMenu.Items.Add(item);
        }

        menu.Items.Add(durationMenu);
        menu.Items.Add(new Separator());

        _startupItem = new MenuItem
        {
            Header = "Lancer au démarrage de Windows",
            IsCheckable = true,
            IsChecked = AutoStartService.IsEnabled
        };
        _startupItem.Click += (_, _) => SetAutoStart(_startupItem.IsChecked);
        menu.Items.Add(_startupItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Quitter SpotOnFly" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        return menu;
    }

    private void SelectPosition(OverlayPosition position)
    {
        foreach (var (key, item) in _positionItems)
        {
            item.IsChecked = key == position;
        }

        _overlayController.SetPosition(position);
    }

    private void SelectSize(OverlaySize size)
    {
        foreach (var (key, item) in _sizeItems)
        {
            item.IsChecked = key == size;
        }

        _overlayController.SetSize(size);
    }

    private void SelectHoldSeconds(int seconds)
    {
        foreach (var (key, item) in _durationItems)
        {
            item.IsChecked = key == seconds;
        }

        _overlayController.SetHoldSeconds(seconds);
    }

    private void SetAutoStart(bool enabled)
    {
        AutoStartService.SetEnabled(enabled);
        _settingsService.Update(settings => settings.RunAtStartup = enabled);

        // Resynchronise la case si l'écriture dans le registre a échoué.
        if (_startupItem is not null)
        {
            _startupItem.IsChecked = AutoStartService.IsEnabled;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SyncMenuState();
            return;
        }

        _ = dispatcher.InvokeAsync(SyncMenuState);
    }

    private void SyncMenuState()
    {
        var settings = _settingsService.Settings;

        if (_enabledItem is not null)
        {
            _enabledItem.IsChecked = settings.Enabled;
        }

        if (_artistItem is not null)
        {
            _artistItem.IsChecked = settings.ShowArtist;
        }

        if (_startupItem is not null)
        {
            _startupItem.IsChecked = AutoStartService.IsEnabled;
        }

        foreach (var (position, item) in _positionItems)
        {
            item.IsChecked = settings.Position == position;
        }

        foreach (var (size, item) in _sizeItems)
        {
            item.IsChecked = settings.Size == size;
        }

        foreach (var (seconds, item) in _durationItems)
        {
            item.IsChecked = Math.Abs(settings.HoldSeconds - seconds) < 0.01;
        }
    }

    private void SetToolTip(string text)
    {
        if (text.Length > 120)
        {
            text = text[..117] + "...";
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.ToolTipText = text;
                }
            });
        }
        else if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = text;
        }
    }

    private static string PositionLabel(OverlayPosition position) => position switch
    {
        OverlayPosition.TopLeft => "Haut gauche",
        OverlayPosition.TopCenter => "Haut centre",
        OverlayPosition.TopRight => "Haut droite",
        OverlayPosition.BottomLeft => "Bas gauche",
        OverlayPosition.BottomCenter => "Bas centre",
        OverlayPosition.BottomRight => "Bas droite",
        _ => position.ToString()
    };

    private static string SizeLabel(OverlaySize size) => size switch
    {
        OverlaySize.Small => "Petite",
        OverlaySize.Large => "Grande",
        _ => "Moyenne"
    };

    private static ImageSource LoadIconSource()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
