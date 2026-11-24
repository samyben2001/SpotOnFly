using System.Windows;
using SpotOnFly.Models;

namespace SpotOnFly.Views;

/// <summary>
/// Calcule la largeur et la position de l'overlay à partir de la zone de travail
/// réelle de l'écran. Logique pure, sans dépendance à la fenêtre WPF, afin de
/// pouvoir être vérifiée par des tests unitaires.
/// </summary>
internal static class OverlayLayoutCalculator
{
    public const double PreferredWidth = 560;
    public const double MinimumWidth = 300;
    public const double CompactWidthThreshold = 480;
    public const double SmallScale = 0.85;
    public const double MediumScale = 1.0;
    public const double LargeScale = 1.20;

    private const double DesiredWidthRatio = 0.4;

    /// <summary>
    /// Détermine la largeur de référence (celle de la taille moyenne) puis la
    /// largeur réelle après application du facteur de taille.
    /// </summary>
    public static OverlayLayoutMetrics Calculate(
        Rect workArea,
        OverlaySize size,
        double edgeMargin)
    {
        var availableWidth = Math.Max(0, workArea.Width - (edgeMargin * 2));

        // Largeur de référence proportionnelle à l'écran, dans les limites
        // 300-560 DIP, puis plafonnée à la place réellement disponible.
        var desiredBaseWidth = Math.Clamp(
            workArea.Width * DesiredWidthRatio,
            MinimumWidth,
            PreferredWidth);

        var scale = GetSizeScale(size);
        var baseWidth = Math.Min(desiredBaseWidth, availableWidth / scale);

        return new OverlayLayoutMetrics(
            baseWidth,
            baseWidth * scale,
            scale,
            baseWidth < CompactWidthThreshold);
    }

    public static double GetSizeScale(OverlaySize size) => size switch
    {
        OverlaySize.Small => SmallScale,
        OverlaySize.Large => LargeScale,
        _ => MediumScale
    };

    /// <summary>
    /// Positionne la fenêtre dans la zone de travail en respectant la marge
    /// demandée sur les bords concernés.
    /// </summary>
    public static Point CalculatePosition(
        Rect workArea,
        OverlayPosition position,
        double edgeMargin,
        double actualWidth,
        double actualHeight)
    {
        var left = position switch
        {
            OverlayPosition.TopLeft or OverlayPosition.BottomLeft
                => workArea.Left + edgeMargin,
            OverlayPosition.TopRight or OverlayPosition.BottomRight
                => workArea.Right - actualWidth - edgeMargin,
            _ => workArea.Left + ((workArea.Width - actualWidth) / 2)
        };

        var top = position switch
        {
            OverlayPosition.TopLeft or OverlayPosition.TopCenter or OverlayPosition.TopRight
                => workArea.Top + edgeMargin,
            _ => workArea.Bottom - actualHeight - edgeMargin
        };

        return new Point(left, top);
    }
}

/// <summary>Résultat du calcul de largeur de l'overlay.</summary>
internal readonly record struct OverlayLayoutMetrics(
    double BaseWidth,
    double TargetWidth,
    double Scale,
    bool IsCompact);
