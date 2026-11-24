using SpotOnFly.Models;

namespace SpotOnFly.Services;

/// <summary>
/// Décisions du contrôleur d'overlay, isolées de la fenêtre WPF et du watcher
/// média pour pouvoir être vérifiées sans interface graphique.
/// </summary>
internal static class OverlayDisplayPolicy
{
    /// <summary>Une piste n'est affichée que si l'overlay est actif.</summary>
    public static bool ShouldShowTrack(TrackInfo? track, bool enabled)
        => enabled && track is not null;

    /// <summary>
    /// Au moment de réactiver l'overlay, on réaffiche la dernière piste connue
    /// (elle est mémorisée même lorsque l'overlay était masqué).
    /// </summary>
    public static bool ShouldRestoreTrack(TrackInfo? rememberedTrack, bool enabled)
        => enabled && rememberedTrack is not null;

    /// <summary>
    /// La recherche en ligne ne sert que si Windows (GSMTC) n'a pas fourni
    /// de pochette pour la piste.
    /// </summary>
    public static bool ShouldLookUpAlbumArt(TrackInfo? track)
        => track is not null && track.Thumbnail is null;

    /// <summary>Une recherche de pochette en cours devient inutile si l'overlay est coupé.</summary>
    public static bool ShouldCancelAlbumArtLookup(bool enabled) => !enabled;
}
