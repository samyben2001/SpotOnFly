using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SpotOnFly.Interop;
using SpotOnFly.Models;
using SpotOnFly.Services;

namespace SpotOnFly.Views;

public partial class OverlayWindow : Window
{
    private static readonly Thickness NormalCardMargin = new(16);
    private static readonly Thickness CompactCardMargin = new(10);
    private static readonly Thickness NormalCardPadding = new(14);
    private static readonly Thickness CompactCardPadding = new(10, 9, 10, 9);

    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _layoutRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };

    private TrackInfo? _currentTrack;
    private int _fadeVersion;
    private bool _disposed;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;

    public TrackInfo? CurrentTrack => _currentTrack;

    public OverlayWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();

        WindowInterop.ConfigureOverlayWindow(this);
        _hideTimer.Tick += OnHideTimerTick;
        _layoutRefreshTimer.Tick += OnLayoutRefreshTick;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        DpiChanged += OnDpiChanged;
        Closed += OnClosed;
    }

    public void ShowTrack(TrackInfo track)
    {
        _fadeVersion++;
        _currentTrack = track;

        TitleText.Text = track.Title;
        UpdateArtistVisibility();

        AlbumArt.BeginAnimation(UIElement.OpacityProperty, null);
        AlbumArt.Opacity = 1;
        AlbumArt.Source = track.Thumbnail;

        PreviousButton.IsEnabled = track.CanSkipPrevious;
        PlayPauseButton.IsEnabled = track.CanPlayPause;
        NextButton.IsEnabled = track.CanSkipNext;
        SetPlaybackState(track.IsPlaying);

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;

        if (!IsVisible)
        {
            Show();
        }

        var workArea = UpdateOverlayLayout();

        AppLog.Info(
            $"Overlay affiché : « {track.Title} » — L={Left}, T={Top}, W={ActualWidth}, H={ActualHeight}, " +
            $"zone={workArea.Width:0}×{workArea.Height:0} DIP, Visible={IsVisible}");

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        StartHideTimer();
    }

    /// <summary>
    /// Applique une pochette chargée après l'affichage de la piste (recherche
    /// en ligne), avec un fondu pour éviter un remplacement brutal du placeholder.
    /// </summary>
    public void UpdateAlbumArt(ImageSource? thumbnail)
    {
        if (_disposed)
        {
            return;
        }

        AlbumArt.BeginAnimation(UIElement.OpacityProperty, null);
        AlbumArt.Source = thumbnail;

        if (thumbnail is null)
        {
            AlbumArt.Opacity = 1;
            return;
        }

        AlbumArt.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        AlbumArt.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public void UpdatePlaybackState(bool isPlaying) => SetPlaybackState(isPlaying);

    public void HideOverlay()
    {
        _fadeVersion++;
        _hideTimer.Stop();
        _layoutRefreshTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Hide();
        Opacity = 0;
    }

    public void ApplySettings()
    {
        UpdateArtistVisibility();

        if (!IsVisible)
        {
            return;
        }

        UpdateOverlayLayout();

        // Si la durée change pendant que l'overlay est visible, on repart sur
        // une nouvelle temporisation avec la valeur choisie.
        var holdSeconds = _settingsService.Settings.HoldSeconds;
        if (_hideTimer.IsEnabled && Math.Abs(_hideTimer.Interval.TotalSeconds - holdSeconds) > 0.01)
        {
            StartHideTimer();
        }
    }

    private void SetPlaybackState(bool isPlaying)
    {
        PlayPauseGlyph.Text = isPlaying ? "\uE769" : "\uE768";
        PlayPauseButton.ToolTip = isPlaying ? "Pause" : "Lecture";
    }

    private void UpdateArtistVisibility()
    {
        var show = _settingsService.Settings.ShowArtist && !string.IsNullOrWhiteSpace(_currentTrack?.Artist);
        ArtistText.Text = show ? _currentTrack!.Artist! : string.Empty;
        ArtistText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(_settingsService.Settings.HoldSeconds);
        _hideTimer.Start();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => _hideTimer.Stop();

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (IsVisible)
        {
            StartHideTimer();
        }
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
        => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClick(object sender, RoutedEventArgs e)
        => NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();

        var version = ++_fadeVersion;
        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (version != _fadeVersion)
            {
                return;
            }

            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Recalcule la largeur utile puis la position de l'overlay en fonction de la
    /// zone de travail réelle de l'écran (résolution, barre des tâches, DPI).
    /// </summary>
    private Rect UpdateOverlayLayout()
    {
        var workArea = DisplayInterop.GetWorkArea(this);
        ApplyResponsiveLayout(workArea);
        UpdateLayout();
        PositionWindow(workArea);
        return workArea;
    }

    private void ApplyResponsiveLayout(Rect workArea)
    {
        var settings = _settingsService.Settings;
        var metrics = OverlayLayoutCalculator.Calculate(
            workArea,
            settings.Size,
            settings.EdgeMargin);

        if (Math.Abs(Width - metrics.TargetWidth) > 0.5)
        {
            Width = metrics.TargetWidth;
        }

        if (Math.Abs(ScaleHost.Width - metrics.BaseWidth) > 0.5)
        {
            ScaleHost.Width = metrics.BaseWidth;
        }

        // La taille est appliquée à toute la carte (textes, pochette, boutons)
        // pour conserver les proportions entre les trois réglages.
        ScaleHost.LayoutTransform = new ScaleTransform(metrics.Scale, metrics.Scale);

        // Sur les écrans étroits, on sacrifie la pochette pour préserver le titre
        // et les boutons plutôt que de laisser l'overlay déborder.
        var compact = metrics.IsCompact;
        AlbumArtBorder.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AlbumArtColumn.Width = new GridLength(compact ? 0 : 56);
        AlbumArtSpacerColumn.Width = new GridLength(compact ? 0 : 14);
        Card.Margin = compact ? CompactCardMargin : NormalCardMargin;
        Card.Padding = compact ? CompactCardPadding : NormalCardPadding;
    }

    private void PositionWindow(Rect workArea)
    {
        var settings = _settingsService.Settings;
        var position = OverlayLayoutCalculator.CalculatePosition(
            workArea,
            settings.Position,
            settings.EdgeMargin,
            ActualWidth,
            ActualHeight);

        Left = position.X;
        Top = position.Y;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleLayoutRefresh();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => ScheduleLayoutRefresh();

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.WorkArea)
            or nameof(SystemParameters.PrimaryScreenWidth)
            or nameof(SystemParameters.PrimaryScreenHeight)
            or nameof(SystemParameters.VirtualScreenWidth)
            or nameof(SystemParameters.VirtualScreenHeight))
        {
            ScheduleLayoutRefresh();
        }
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => ScheduleLayoutRefresh();

    /// <summary>
    /// Regroupe les notifications système (souvent émises en rafale lors d'un
    /// changement de résolution ou de DPI) et laisse WPF terminer sa mise en page
    /// avant de repositionner la fenêtre.
    /// </summary>
    private void ScheduleLayoutRefresh()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ScheduleLayoutRefresh));
            return;
        }

        if (_disposed)
        {
            return;
        }

        _layoutRefreshTimer.Stop();
        _layoutRefreshTimer.Start();
    }

    private void OnLayoutRefreshTick(object? sender, EventArgs e)
    {
        _layoutRefreshTimer.Stop();

        if (_disposed || !IsVisible)
        {
            return;
        }

        var workArea = UpdateOverlayLayout();
        AppLog.Info(
            $"Écran modifié : overlay repositionné — zone={workArea.Width:0}×{workArea.Height:0} DIP, " +
            $"L={Left:0.#}, T={Top:0.#}, W={ActualWidth:0}.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _layoutRefreshTimer.Stop();

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        DpiChanged -= OnDpiChanged;
        Closed -= OnClosed;
    }
}
