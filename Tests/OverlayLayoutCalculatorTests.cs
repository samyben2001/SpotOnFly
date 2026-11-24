using System.Windows;
using SpotOnFly.Models;
using SpotOnFly.Views;

namespace SpotOnFly.Tests;

public sealed class OverlayLayoutCalculatorTests
{
    private static readonly Rect FullHdWorkArea = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(OverlaySize.Small, 0.85)]
    [InlineData(OverlaySize.Medium, 1.0)]
    [InlineData(OverlaySize.Large, 1.2)]
    public void GetSizeScale_ReturnsExpectedScale(OverlaySize size, double expected)
        => Assert.Equal(expected, OverlayLayoutCalculator.GetSizeScale(size), 3);

    [Fact]
    public void GetSizeScale_UndefinedValue_FallsBackToMedium()
        => Assert.Equal(1.0, OverlayLayoutCalculator.GetSizeScale((OverlaySize)42), 3);

    [Theory]
    [InlineData(OverlaySize.Small, 476)]
    [InlineData(OverlaySize.Medium, 560)]
    [InlineData(OverlaySize.Large, 672)]
    public void Calculate_WideScreen_UsesPreferredBaseWidth(
        OverlaySize size,
        double expectedTargetWidth)
    {
        var metrics = OverlayLayoutCalculator.Calculate(FullHdWorkArea, size, 8);

        Assert.Equal(560, metrics.BaseWidth, 3);
        Assert.Equal(expectedTargetWidth, metrics.TargetWidth, 3);
        Assert.False(metrics.IsCompact);
    }

    [Fact]
    public void Calculate_NarrowScreen_ShrinksAndSwitchesToCompact()
    {
        var metrics = OverlayLayoutCalculator.Calculate(
            new Rect(0, 0, 800, 600),
            OverlaySize.Medium,
            8);

        // 800 * 0.4 = 320 DIP, sous le seuil compact (480).
        Assert.Equal(320, metrics.BaseWidth, 3);
        Assert.Equal(320, metrics.TargetWidth, 3);
        Assert.True(metrics.IsCompact);
    }

    [Fact]
    public void Calculate_VeryNarrowScreen_ClampsTargetWidthToAvailableSpace()
    {
        var workArea = new Rect(0, 0, 320, 200);
        var metrics = OverlayLayoutCalculator.Calculate(workArea, OverlaySize.Large, 8);

        // La largeur de référence est réduite pour que la carte agrandie
        // tienne exactement dans la zone utile (320 - 2 * 8 = 304 DIP).
        Assert.Equal(304, metrics.TargetWidth, 3);
        Assert.True(metrics.TargetWidth <= workArea.Width - 16);
        Assert.True(metrics.IsCompact);
    }

    [Fact]
    public void Calculate_ScreenNarrowerThanMargins_DoesNotProduceNegativeWidth()
    {
        var metrics = OverlayLayoutCalculator.Calculate(
            new Rect(0, 0, 10, 10),
            OverlaySize.Medium,
            8);

        Assert.Equal(0, metrics.BaseWidth, 3);
        Assert.Equal(0, metrics.TargetWidth, 3);
        Assert.True(metrics.IsCompact);
    }

    [Theory]
    [InlineData(OverlayPosition.TopLeft, 8, 8)]
    [InlineData(OverlayPosition.TopCenter, 680, 8)]
    [InlineData(OverlayPosition.TopRight, 1352, 8)]
    [InlineData(OverlayPosition.BottomLeft, 8, 932)]
    [InlineData(OverlayPosition.BottomCenter, 680, 932)]
    [InlineData(OverlayPosition.BottomRight, 1352, 932)]
    public void CalculatePosition_FullHd_PlacesOverlayAtExpectedCorner(
        OverlayPosition position,
        double expectedLeft,
        double expectedTop)
    {
        var point = OverlayLayoutCalculator.CalculatePosition(
            FullHdWorkArea,
            position,
            8,
            560,
            100);

        Assert.Equal(expectedLeft, point.X, 3);
        Assert.Equal(expectedTop, point.Y, 3);
    }

    [Fact]
    public void CalculatePosition_SecondaryMonitor_UsesWorkAreaOffset()
    {
        // Écran secondaire placé à gauche et au-dessus du moniteur principal.
        var workArea = new Rect(-1920, -200, 1920, 1080);

        var topLeft = OverlayLayoutCalculator.CalculatePosition(
            workArea,
            OverlayPosition.TopLeft,
            8,
            560,
            100);

        var bottomRight = OverlayLayoutCalculator.CalculatePosition(
            workArea,
            OverlayPosition.BottomRight,
            8,
            560,
            100);

        Assert.Equal(-1912, topLeft.X, 3);
        Assert.Equal(-192, topLeft.Y, 3);
        Assert.Equal(-568, bottomRight.X, 3);
        Assert.Equal(772, bottomRight.Y, 3);
    }

    [Fact]
    public void CalculatePosition_UndefinedPosition_FallsBackToBottomCenter()
    {
        var point = OverlayLayoutCalculator.CalculatePosition(
            FullHdWorkArea,
            (OverlayPosition)42,
            8,
            560,
            100);

        Assert.Equal(680, point.X, 3);
        Assert.Equal(932, point.Y, 3);
    }
}
