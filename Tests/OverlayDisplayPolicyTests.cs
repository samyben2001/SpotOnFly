using System.Windows.Media;
using SpotOnFly.Models;
using SpotOnFly.Services;

namespace SpotOnFly.Tests;

public sealed class OverlayDisplayPolicyTests
{
    [Fact]
    public void ShouldShowTrack_OnlyWhenEnabledAndTrackPresent()
    {
        var track = CreateTrack();

        Assert.True(OverlayDisplayPolicy.ShouldShowTrack(track, enabled: true));
        Assert.False(OverlayDisplayPolicy.ShouldShowTrack(track, enabled: false));
        Assert.False(OverlayDisplayPolicy.ShouldShowTrack(null, enabled: true));
    }

    [Fact]
    public void ShouldRestoreTrack_OnlyWhenEnabledAndTrackRemembered()
    {
        var track = CreateTrack();

        Assert.True(OverlayDisplayPolicy.ShouldRestoreTrack(track, enabled: true));
        Assert.False(OverlayDisplayPolicy.ShouldRestoreTrack(track, enabled: false));
        Assert.False(OverlayDisplayPolicy.ShouldRestoreTrack(null, enabled: true));
    }

    [Fact]
    public void ShouldLookUpAlbumArt_OnlyWhenThumbnailMissing()
    {
        Assert.True(OverlayDisplayPolicy.ShouldLookUpAlbumArt(CreateTrack()));
        Assert.False(OverlayDisplayPolicy.ShouldLookUpAlbumArt(CreateTrack(new DrawingImage())));
        Assert.False(OverlayDisplayPolicy.ShouldLookUpAlbumArt(null));
    }

    [Fact]
    public void ShouldCancelAlbumArtLookup_OnlyWhenOverlayDisabled()
    {
        Assert.True(OverlayDisplayPolicy.ShouldCancelAlbumArtLookup(enabled: false));
        Assert.False(OverlayDisplayPolicy.ShouldCancelAlbumArtLookup(enabled: true));
    }

    private static TrackInfo CreateTrack(ImageSource? thumbnail = null)
        => new("Titre", "Artiste", "Album", "app", thumbnail);
}
