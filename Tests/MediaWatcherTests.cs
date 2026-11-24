using SpotOnFly.Services;

namespace SpotOnFly.Tests;

public sealed class MediaWatcherTests
{
    [Theory]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Mozilla.Chrome", "chrome")]
    public void IsIgnored_MatchesCaseInsensitively(string appId, string ignored)
        => Assert.True(MediaWatcher.IsIgnored(appId, [ignored]));

    [Fact]
    public void IsIgnored_NoMatch_ReturnsFalse()
        => Assert.False(MediaWatcher.IsIgnored("Spotify", ["deezer", "vlc"]));

    [Fact]
    public void IsIgnored_SkipsBlankEntries()
        => Assert.False(MediaWatcher.IsIgnored("Spotify", ["", "   "]));

    [Fact]
    public void IsIgnored_EmptyOrNullList_ReturnsFalse()
    {
        Assert.False(MediaWatcher.IsIgnored("Spotify", []));
        Assert.False(MediaWatcher.IsIgnored("Spotify", null));
    }

    [Fact]
    public void IsIgnored_EmptyAppId_OnlyMatchesBlankFreeEntry()
        => Assert.False(MediaWatcher.IsIgnored(string.Empty, ["spotify"]));

    [Fact]
    public void BuildTrackSignature_IsStableAndSeparatesFields()
    {
        var first = MediaWatcher.BuildTrackSignature("app", "Titre", "Artiste");
        var second = MediaWatcher.BuildTrackSignature("app", "Titre", "Artiste");

        Assert.Equal(first, second);
        Assert.Equal("app\u001fTitre\u001fArtiste", first);
    }

    [Fact]
    public void BuildTrackSignature_DistinguishesArtistChanges()
    {
        var first = MediaWatcher.BuildTrackSignature("app", "Titre", "Artiste A");
        var second = MediaWatcher.BuildTrackSignature("app", "Titre", "Artiste B");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildTrackSignature_MissingArtist_KeepsEmptyField()
        => Assert.Equal("app\u001fTitre\u001f", MediaWatcher.BuildTrackSignature("app", "Titre", null));
}
