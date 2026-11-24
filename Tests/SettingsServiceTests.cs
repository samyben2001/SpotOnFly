using SpotOnFly.Models;
using SpotOnFly.Services;

namespace SpotOnFly.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Deserialize_InvalidPosition_FallsBackWithoutLosingOtherSettings()
    {
        const string json = """
            {
              "Enabled": false,
              "Position": "Somewhere",
              "Size": "Large",
              "HoldSeconds": 2,
              "EdgeMargin": 12,
              "ShowArtist": false,
              "RunAtStartup": true,
              "IgnoredApps": [ "Spotify" ]
            }
            """;

        var settings = SettingsService.Deserialize(json);

        Assert.Equal(OverlayPosition.BottomCenter, settings.Position);
        Assert.False(settings.Enabled);
        Assert.Equal(OverlaySize.Large, settings.Size);
        Assert.Equal(2d, settings.HoldSeconds);
        Assert.Equal(12d, settings.EdgeMargin);
        Assert.False(settings.ShowArtist);
        Assert.True(settings.RunAtStartup);
        Assert.Equal(new[] { "Spotify" }, settings.IgnoredApps);
    }

    [Fact]
    public void Deserialize_InvalidSize_FallsBackToMedium()
    {
        var settings = SettingsService.Deserialize(
            """{ "Position": "TopLeft", "Size": "Gigantic" }""");

        Assert.Equal(OverlaySize.Medium, settings.Size);
        Assert.Equal(OverlayPosition.TopLeft, settings.Position);
    }

    [Fact]
    public void Deserialize_ValidEnums_AreReadIgnoringCase()
    {
        var settings = SettingsService.Deserialize(
            """{ "Position": "bottomright", "Size": "small", "HoldSeconds": 5 }""");

        Assert.Equal(OverlayPosition.BottomRight, settings.Position);
        Assert.Equal(OverlaySize.Small, settings.Size);
        Assert.Equal(5d, settings.HoldSeconds);
    }

    [Fact]
    public void Sanitize_ClampsValuesAndReplacesUndefinedEnums()
    {
        var settings = new AppSettings
        {
            Position = (OverlayPosition)42,
            Size = (OverlaySize)42,
            HoldSeconds = 99,
            EdgeMargin = -3,
            IgnoredApps = null!
        };

        var sanitized = SettingsService.Sanitize(settings);

        Assert.Equal(OverlayPosition.BottomCenter, sanitized.Position);
        Assert.Equal(OverlaySize.Medium, sanitized.Size);
        Assert.Equal(5d, sanitized.HoldSeconds);
        Assert.Equal(0d, sanitized.EdgeMargin);
        Assert.NotNull(sanitized.IgnoredApps);
        Assert.Empty(sanitized.IgnoredApps);
    }
}
