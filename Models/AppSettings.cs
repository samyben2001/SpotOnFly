namespace SpotOnFly.Models;

public enum OverlayPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public enum OverlaySize
{
    Small,
    Medium,
    Large
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    public OverlayPosition Position { get; set; } = OverlayPosition.BottomCenter;

    public OverlaySize Size { get; set; } = OverlaySize.Medium;

    /// <summary>Durée d'affichage de l'overlay à chaque changement de piste.</summary>
    public double HoldSeconds { get; set; } = 4;

    /// <summary>Distance (en DIP) entre l'overlay et le bord de l'écran.</summary>
    public double EdgeMargin { get; set; } = 8;

    public bool ShowArtist { get; set; } = true;

    public bool RunAtStartup { get; set; } = false;

    /// <summary>Identifiants d'applications dont les médias sont ignorés.</summary>
    public List<string> IgnoredApps { get; set; } = [];
}
