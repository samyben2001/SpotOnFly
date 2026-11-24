using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using SpotOnFly.Services;

namespace SpotOnFly.Tests;

public sealed class AlbumArtServiceTests
{
    [Theory]
    [InlineData("Tueur de dragon (Vent)", "tueur de dragon")]
    [InlineData("Régal", "regal")]
    [InlineData("L'Évadé [Deluxe Edition]", "levade")]
    [InlineData("  Alpha   5.20  ", "alpha 520")]
    public void Normalize_RemovesAccentsBracketsAndPunctuation(string value, string expected)
        => Assert.Equal(expected, AlbumArtService.Normalize(value));

    [Fact]
    public void ScoreCandidate_ExactMetadata_ScoresHigh()
    {
        var score = AlbumArtService.ScoreCandidate(
            "Régal",
            "Aketo",
            "Régal",
            "Régal",
            "Aketo",
            "Régal");

        Assert.Equal(18, score);
    }

    [Fact]
    public void ScoreCandidate_ExactTitleButDifferentArtist_IsRejectedByThreshold()
    {
        var score = AlbumArtService.ScoreCandidate(
            "Régal",
            "Aketo",
            null,
            "Régal",
            "Someone Else",
            null);

        Assert.Equal(6, score);
        Assert.True(score < 8);
    }

    [Fact]
    public void ScoreCandidate_SimilarTitleAndMatchingArtist_IsAccepted()
    {
        var score = AlbumArtService.ScoreCandidate(
            "Tueur de dragon (Vent)",
            "ISHA",
            null,
            "Tueur de dragon",
            "ISHA",
            null);

        Assert.True(score >= 8);
    }

    [Fact]
    public void ScoreCandidate_UnrelatedTitle_IsRejected()
    {
        var score = AlbumArtService.ScoreCandidate(
            "Régal",
            "Aketo",
            null,
            "Something Completely Different",
            "Aketo",
            null);

        Assert.Equal(-1, score);
    }

    [Fact]
    public void ScoreCandidate_SimilarAlbumAddsPoints()
    {
        var withoutAlbum = AlbumArtService.ScoreCandidate(
            "Régal",
            "Aketo",
            null,
            "Régal",
            "Aketo",
            null);

        var withAlbum = AlbumArtService.ScoreCandidate(
            "Régal",
            "Aketo",
            "Régal",
            "Régal",
            "Aketo",
            "Regal (Deluxe)");

        Assert.Equal(withoutAlbum + 3, withAlbum);
    }

    [Fact]
    public async Task NegativeCache_RoundTripsUntilExpiry()
    {
        var directory = CreateTempDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var service = new AlbumArtService(directory, () => now);
            const string key = "artist|title|album";

            Assert.False(await service.TryReadNegativeCacheAsync(key, CancellationToken.None));

            await service.TryWriteNegativeCacheAsync(key, CancellationToken.None);
            Assert.True(await service.TryReadNegativeCacheAsync(key, CancellationToken.None));

            now = now
                .Add(AlbumArtService.NegativeCacheLifetime)
                .Add(TimeSpan.FromSeconds(1));

            Assert.False(await service.TryReadNegativeCacheAsync(key, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(directory, "*.miss"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void OnlineLookupPause_ExpiresWithClock()
    {
        var directory = CreateTempDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var service = new AlbumArtService(directory, () => now);

            Assert.False(service.IsOnlineLookupPaused);

            service.PauseOnlineLookups(TimeSpan.FromMinutes(10));
            Assert.True(service.IsOnlineLookupPaused);
            Assert.True(service.GetRemainingOnlinePause() > TimeSpan.FromMinutes(9));

            now = now.AddMinutes(11);
            Assert.False(service.IsOnlineLookupPaused);
            Assert.Equal(TimeSpan.Zero, service.GetRemainingOnlinePause());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ResolvePauseDuration_DefaultsToTenMinutes()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Equal(
            AlbumArtService.DefaultRateLimitPause,
            AlbumArtService.ResolvePauseDuration(response));
    }

    [Theory]
    [InlineData(120, 2)]
    [InlineData(5, 1)]
    [InlineData(7200, 60)]
    public void ResolvePauseDuration_RespectsAndClampsRetryAfter(
        int retryAfterSeconds,
        int expectedMinutes)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            TimeSpan.FromSeconds(retryAfterSeconds));

        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            AlbumArtService.ResolvePauseDuration(response));
    }

    [Fact]
    public void PruneDiskCache_KeepsNewestArtworksUnderLimit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fileCount = AlbumArtService.MaxCachedArtworks + 5;

            for (var index = 0; index < fileCount; index++)
            {
                var path = Path.Combine(directory, $"{index:D4}.img");
                File.WriteAllBytes(path, [0]);
                File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(index));
            }

            var service = new AlbumArtService(directory);
            service.PruneDiskCache();

            var remaining = Directory
                .GetFiles(directory, "*.img")
                .Select(Path.GetFileName)
                .ToList();

            Assert.Equal(AlbumArtService.MaxCachedArtworks, remaining.Count);
            Assert.DoesNotContain("0000.img", remaining);
            Assert.Contains($"{fileCount - 1:D4}.img", remaining);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void PruneDiskCache_RemovesExpiredNegativeEntries()
    {
        var directory = CreateTempDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var service = new AlbumArtService(directory, () => now);
            var path = Path.Combine(directory, "expired.miss");

            File.WriteAllText(path, "0");
            File.SetLastWriteTimeUtc(
                path,
                now.Subtract(AlbumArtService.NegativeCacheLifetime).AddDays(-1).UtcDateTime);

            service.PruneDiskCache();
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SpotOnFly.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Un fichier de test encore verrouillé ne doit pas masquer le résultat.
        }
    }
}
