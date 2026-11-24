using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotOnFly.Models;

namespace SpotOnFly.Services;

/// <summary>
/// Récupère la pochette d'un morceau lorsque Windows (GSMTC) n'en fournit pas.
/// Les métadonnées sont recherchées sur l'API publique Deezer, puis l'image est
/// mise en cache en mémoire et sur disque pour éviter les requêtes répétées.
/// </summary>
public sealed class AlbumArtService
{
    private const int MemoryCacheCapacity = 64;
    internal const int MaxCachedArtworks = 500;
    internal const long MaxCachedArtworkBytes = 50L * 1024 * 1024;
    private const int MaxArtworkBytes = 10 * 1024 * 1024;
    private const int MaxNegativeCacheEntries = 1000;
    private const int ProxyMode = 2;
    private static readonly TimeSpan DirectRetryInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromDays(7);
    internal static readonly TimeSpan DefaultRateLimitPause = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MinRateLimitPause = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaxRateLimitPause = TimeSpan.FromMinutes(60);

    private static readonly HttpClient DirectHttp = CreateHttpClient(useProxy: false);
    private static readonly HttpClient ProxyHttp = CreateHttpClient(useProxy: true);
    private static readonly Regex BracketPattern = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private static int _connectionMode;
    private static long _directRetryAt;

    private readonly Dictionary<string, ImageSource?> _memoryCache = new(StringComparer.Ordinal);
    private readonly object _memoryCacheLock = new();
    private readonly string _cacheDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private long _onlineLookupPausedUntil;

    public AlbumArtService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpotOnFly",
            "artwork"))
    {
    }

    internal AlbumArtService(string cacheDirectory, Func<DateTimeOffset>? clock = null)
    {
        _cacheDirectory = cacheDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Retourne la pochette GSMTC si elle existe, sinon tente une recherche en
    /// ligne. Retourne <c>null</c> si aucune pochette n'est disponible.
    /// </summary>
    public async Task<ImageSource?> GetAlbumArtAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (track.Thumbnail is not null)
        {
            return track.Thumbnail;
        }

        var key = BuildCacheKey(track);
        if (key.Length == 0)
        {
            return null;
        }

        if (TryGetFromMemory(key, out var cached))
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var diskImage = await TryReadDiskCacheAsync(key, cancellationToken).ConfigureAwait(false);
        if (diskImage is not null)
        {
            StoreInMemory(key, diskImage);
            return diskImage;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (await TryReadNegativeCacheAsync(key, cancellationToken).ConfigureAwait(false))
        {
            AppLog.Info($"Pochette déjà recherchée sans résultat : « {track.Title} » — {track.Artist}.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var lookup = await TryDownloadCoverAsync(track, cancellationToken).ConfigureAwait(false);
        var coverBytes = lookup.Bytes;
        if (coverBytes is null)
        {
            if (lookup.DefinitiveMiss)
            {
                await TryWriteNegativeCacheAsync(key, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var image = DecodeImage(coverBytes);
        if (image is null)
        {
            return null;
        }

        StoreInMemory(key, image);
        await TryWriteDiskCacheAsync(key, coverBytes, cancellationToken).ConfigureAwait(false);
        return image;
    }

    private static HttpClient CreateHttpClient(bool useProxy)
    {
        // Sur Windows 11, forcer TLS 1.3 : certains CDN (Deezer/Akamai) rendent
        // le handshake TLS 1.2 très lent (plusieurs secondes). Le client proxy
        // reste compatible TLS 1.2 pour les systèmes ou serveurs plus anciens.
        var protocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        if (!useProxy && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            protocols = SslProtocols.Tls13;
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectPreferredAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = protocols
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(useProxy ? 8 : 5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent());
        return client;
    }

    private static string BuildUserAgent()
    {
        var assembly = typeof(AlbumArtService).Assembly;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "dev";

        // Retire les métadonnées de build (par exemple « 0.1.0+abc123 »).
        var metadataIndex = version.IndexOf('+');
        if (metadataIndex >= 0)
        {
            version = version[..metadataIndex];
        }

        return $"SpotOnFly/{version}";
    }

    /// <summary>
    /// Le proxy système Windows (souvent en détection automatique) peut ajouter
    /// plusieurs secondes à chaque requête. On tente donc l'accès direct d'abord,
    /// puis on bascule sur le proxy si le réseau l'exige.
    /// </summary>
    private static async Task<HttpResponseMessage> GetWithFallbackAsync(
        string url,
        CancellationToken cancellationToken)
    {
        // Même quand le proxy est actif, on retente régulièrement l'accès direct :
        // une seule panne réseau ne doit pas condamner la session au proxy.
        var tryDirect = Volatile.Read(ref _connectionMode) != ProxyMode ||
                        Environment.TickCount64 >= Volatile.Read(ref _directRetryAt);

        if (tryDirect)
        {
            try
            {
                var response = await DirectHttp
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (Volatile.Read(ref _connectionMode) == ProxyMode)
                {
                    AppLog.Info("Accès direct rétabli.");
                }

                Volatile.Write(ref _connectionMode, 0);
                return response;
            }
            catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
            {
                AppLog.Info("Accès direct indisponible, tentative via le proxy système.");

                try
                {
                    var response = await ProxyHttp
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    MarkProxyMode();
                    return response;
                }
                catch
                {
                    Volatile.Write(ref _connectionMode, 0);
                    throw;
                }
            }
        }

        try
        {
            return await ProxyHttp
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
        {
            Volatile.Write(ref _connectionMode, 0);
            throw;
        }
    }

    private static void MarkProxyMode()
    {
        Volatile.Write(ref _connectionMode, ProxyMode);
        Volatile.Write(
            ref _directRetryAt,
            Environment.TickCount64 + (long)DirectRetryInterval.TotalMilliseconds);
    }

    private static bool IsConnectionFailure(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException
           || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>
    /// Certains réseaux ont un chemin IPv6 défaillant vers les CDN : on privilégie
    /// IPv4 puis on retombe sur IPv6 si nécessaire.
    /// </summary>
    private static async ValueTask<Stream> ConnectPreferredAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns
            .GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);

        var ordered = addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        if (ordered.Length == 0)
        {
            throw new IOException($"Aucune adresse pour {context.DnsEndPoint.Host}.");
        }

        Exception? lastError = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));

                await socket
                    .ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token)
                    .ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw lastError ?? new IOException($"Connexion impossible à {context.DnsEndPoint.Host}.");
    }

    private static string BuildCacheKey(TrackInfo track)
    {
        var title = Normalize(track.Title);
        if (title.Length == 0)
        {
            return string.Empty;
        }

        var artist = Normalize(track.Artist);
        var album = Normalize(track.Album);
        return $"{artist}|{title}|{album}";
    }

    private bool TryGetFromMemory(string key, out ImageSource? image)
    {
        lock (_memoryCacheLock)
        {
            return _memoryCache.TryGetValue(key, out image);
        }
    }

    private void StoreInMemory(string key, ImageSource image)
    {
        lock (_memoryCacheLock)
        {
            if (_memoryCache.Count >= MemoryCacheCapacity)
            {
                _memoryCache.Clear();
            }

            _memoryCache[key] = image;
        }
    }

    private string GetCachePath(string key) => GetCachePath(key, ".img");

    private string GetNegativeCachePath(string key) => GetCachePath(key, ".miss");

    private string GetCachePath(string key, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, hash + extension);
    }

    private async Task<ImageSource?> TryReadDiskCacheAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var path = GetCachePath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                TryDelete(path);
                return null;
            }

            var image = DecodeImage(bytes);
            if (image is null)
            {
                TryDelete(path);
            }

            return image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            return null;
        }
    }

    private async Task TryWriteDiskCacheAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = GetCachePath(key);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            TryDelete(GetNegativeCachePath(key));
            PruneDiskCache();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Le morceau a changé entre-temps : le cache n'est pas indispensable.
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    internal async Task<bool> TryReadNegativeCacheAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var path = GetNegativeCachePath(key);
            if (!File.Exists(path))
            {
                return false;
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAt) &&
                expiresAt > _clock().ToUnixTimeMilliseconds())
            {
                return true;
            }

            TryDelete(path);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            return false;
        }
    }

    internal async Task TryWriteNegativeCacheAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var expiresAt = _clock().Add(NegativeCacheLifetime).ToUnixTimeMilliseconds();
            var path = GetNegativeCachePath(key);
            await File
                .WriteAllTextAsync(path, expiresAt.ToString(CultureInfo.InvariantCulture), cancellationToken)
                .ConfigureAwait(false);
            PruneNegativeCache();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Le morceau a changé entre-temps : le cache négatif est facultatif.
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    private void PruneNegativeCache()
    {
        try
        {
            var directory = new DirectoryInfo(_cacheDirectory);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory.GetFiles("*.miss");
            var excess = files.Length - MaxNegativeCacheEntries;
            if (excess <= 0)
            {
                return;
            }

            foreach (var file in files.OrderBy(entry => entry.LastWriteTimeUtc).Take(excess))
            {
                TryDelete(file.FullName);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    internal bool IsOnlineLookupPaused
        => _clock().ToUnixTimeMilliseconds() < Volatile.Read(ref _onlineLookupPausedUntil);

    internal TimeSpan GetRemainingOnlinePause()
    {
        var remaining = Volatile.Read(ref _onlineLookupPausedUntil) - _clock().ToUnixTimeMilliseconds();
        return remaining > 0 ? TimeSpan.FromMilliseconds(remaining) : TimeSpan.Zero;
    }

    internal void PauseOnlineLookups(TimeSpan duration)
    {
        var until = _clock().Add(ClampPauseDuration(duration)).ToUnixTimeMilliseconds();
        Volatile.Write(ref _onlineLookupPausedUntil, until);
    }

    private void PauseOnlineLookups(HttpResponseMessage response)
    {
        var duration = ResolvePauseDuration(response);
        PauseOnlineLookups(duration);
        AppLog.Info(
            $"Limite de l'API Deezer atteinte (HTTP 429) : recherches en ligne " +
            $"suspendues {duration.TotalMinutes:0} min.");
    }

    internal static TimeSpan ResolvePauseDuration(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return ClampPauseDuration(delta);
        }

        if (retryAfter?.Date is { } date)
        {
            return ClampPauseDuration(date - DateTimeOffset.UtcNow);
        }

        return DefaultRateLimitPause;
    }

    private static TimeSpan ClampPauseDuration(TimeSpan duration)
    {
        if (duration < MinRateLimitPause)
        {
            return MinRateLimitPause;
        }

        return duration > MaxRateLimitPause ? MaxRateLimitPause : duration;
    }

    private async Task<CoverLookupResult> TryDownloadCoverAsync(
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        try
        {
            var terms = new[] { track.Artist, track.Title }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            var query = string.Join(' ', terms);
            if (query.Length == 0)
            {
                return CoverLookupResult.Miss();
            }

            if (IsOnlineLookupPaused)
            {
                AppLog.Info(
                    $"Recherche de pochette ignorée : pause anti-limite API active " +
                    $"({GetRemainingOnlinePause().TotalMinutes:0} min restantes).");
                return CoverLookupResult.TransientFailure();
            }

            AppLog.Info($"Recherche de pochette en ligne pour « {track.Title} » — {track.Artist}.");

            var lookupStopwatch = Stopwatch.StartNew();
            var searchUrl = "https://api.deezer.com/search?q=" + Uri.EscapeDataString(query) + "&limit=10";
            using var searchResponse = await GetWithFallbackAsync(searchUrl, cancellationToken)
                .ConfigureAwait(false);

            if (searchResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                PauseOnlineLookups(searchResponse);
                return CoverLookupResult.TransientFailure();
            }

            if (!searchResponse.IsSuccessStatusCode)
            {
                AppLog.Info($"Recherche de pochette indisponible (HTTP {(int)searchResponse.StatusCode}).");
                return CoverLookupResult.TransientFailure();
            }

            var json = await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var results = JsonSerializer.Deserialize<DeezerSearchResponse>(json);
            var best = SelectBestMatch(track, results?.Data);
            if (best is null)
            {
                AppLog.Info($"Aucune pochette en ligne pour « {track.Title} » — {track.Artist}.");
                return CoverLookupResult.Miss();
            }

            var coverUrl = best.GetCoverUrl();
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                AppLog.Info($"Aucune pochette en ligne pour « {track.Title} » — {track.Artist}.");
                return CoverLookupResult.Miss();
            }

            using var coverResponse = await GetWithFallbackAsync(coverUrl, cancellationToken)
                .ConfigureAwait(false);

            if (coverResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                PauseOnlineLookups(coverResponse);
                return CoverLookupResult.TransientFailure();
            }

            if (!coverResponse.IsSuccessStatusCode)
            {
                AppLog.Info($"Téléchargement de pochette impossible (HTTP {(int)coverResponse.StatusCode}).");
                return CoverLookupResult.TransientFailure();
            }

            var bytes = await coverResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxArtworkBytes)
            {
                return CoverLookupResult.TransientFailure();
            }

            lookupStopwatch.Stop();
            AppLog.Info(
                $"Pochette en ligne trouvée : « {track.Title} » — {track.Artist} " +
                $"({lookupStopwatch.ElapsedMilliseconds} ms).");
            return CoverLookupResult.Found(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            return CoverLookupResult.TransientFailure();
        }
    }

    private static DeezerTrack? SelectBestMatch(TrackInfo track, IEnumerable<DeezerTrack>? candidates)
    {
        if (candidates is null)
        {
            return null;
        }

        DeezerTrack? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = ScoreCandidate(track, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= 8 ? best : null;
    }

    private static int ScoreCandidate(TrackInfo track, DeezerTrack candidate)
        => ScoreCandidate(
            track.Title,
            track.Artist,
            track.Album,
            candidate.Title,
            candidate.Artist?.Name,
            candidate.Album?.Title);

    /// <summary>
    /// Variante testable du calcul de correspondance : les métadonnées Deezer
    /// sont reçues sous forme de chaînes, sans exposer les DTO privés.
    /// </summary>
    internal static int ScoreCandidate(
        string? titleValue,
        string? artistValue,
        string? albumValue,
        string? candidateTitleValue,
        string? candidateArtistValue,
        string? candidateAlbumValue)
    {
        var title = Normalize(titleValue);
        var candidateTitle = Normalize(candidateTitleValue);
        if (title.Length == 0 || candidateTitle.Length == 0)
        {
            return -1;
        }

        int score;
        if (candidateTitle == title)
        {
            score = 10;
        }
        else if (candidateTitle.Contains(title, StringComparison.Ordinal) ||
                 title.Contains(candidateTitle, StringComparison.Ordinal))
        {
            score = 6;
        }
        else
        {
            var titleSimilarity = TokenSimilarity(title, candidateTitle);
            if (titleSimilarity < 0.6)
            {
                return -1;
            }

            score = (int)Math.Round(titleSimilarity * 6);
        }

        var artist = Normalize(artistValue);
        var candidateArtist = Normalize(candidateArtistValue);
        if (artist.Length > 0 && candidateArtist.Length > 0)
        {
            if (candidateArtist == artist ||
                candidateArtist.Contains(artist, StringComparison.Ordinal) ||
                artist.Contains(candidateArtist, StringComparison.Ordinal))
            {
                score += 5;
            }
            else
            {
                var artistSimilarity = TokenSimilarity(artist, candidateArtist);
                if (artistSimilarity >= 0.5)
                {
                    score += 3;
                }
                else if (artistSimilarity > 0)
                {
                    score += 1;
                }
                else
                {
                    // Évite d'afficher la pochette d'une reprise ou d'un homonyme.
                    score -= 4;
                }
            }
        }

        var album = Normalize(albumValue);
        var candidateAlbum = Normalize(candidateAlbumValue);
        if (album.Length > 0 && candidateAlbum.Length > 0)
        {
            if (candidateAlbum == album ||
                candidateAlbum.Contains(album, StringComparison.Ordinal) ||
                album.Contains(candidateAlbum, StringComparison.Ordinal))
            {
                score += 3;
            }
            else if (TokenSimilarity(album, candidateAlbum) >= 0.5)
            {
                score += 1;
            }
        }

        return score;
    }

    private static double TokenSimilarity(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        var rightSet = rightTokens.ToHashSet(StringComparer.Ordinal);
        var common = leftTokens.Count(rightSet.Contains);
        return common / (double)Math.Max(leftTokens.Length, rightTokens.Length);
    }

    /// <summary>
    /// Retire accents, ponctuation et suffixes entre parenthèses pour comparer
    /// les métadonnées GSMTC à celles de Deezer.
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = BracketPattern.Replace(value, " ").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }
        }

        return WhitespacePattern.Replace(builder.ToString(), " ").Trim();
    }

    internal void PruneDiskCache()
    {
        try
        {
            var directory = new DirectoryInfo(_cacheDirectory);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory
                .GetFiles("*.img")
                .OrderBy(entry => entry.LastWriteTimeUtc)
                .ToList();

            var totalBytes = files.Sum(file => file.Length);
            var remaining = files.Count;

            foreach (var file in files)
            {
                if (remaining <= MaxCachedArtworks && totalBytes <= MaxCachedArtworkBytes)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    remaining--;
                    totalBytes -= length;
                }
                catch
                {
                    // Un fichier verrouillé sera supprimé lors du prochain passage.
                }
            }

            // Les résultats négatifs expirés n'ont plus besoin d'occuper le disque.
            var cutoff = _clock().Subtract(NegativeCacheLifetime).UtcDateTime;
            foreach (var file in directory.GetFiles("*.miss"))
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(file.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    private readonly record struct CoverLookupResult(byte[]? Bytes, bool DefinitiveMiss)
    {
        public static CoverLookupResult Found(byte[] bytes) => new(bytes, false);

        public static CoverLookupResult Miss() => new(null, true);

        public static CoverLookupResult TransientFailure() => new(null, false);
    }

    private static ImageSource? DecodeImage(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Un cache corrompu sera simplement remplacé plus tard.
        }
    }

    private sealed class DeezerSearchResponse
    {
        [JsonPropertyName("data")]
        public List<DeezerTrack>? Data { get; set; }
    }

    private sealed class DeezerTrack
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("artist")]
        public DeezerArtist? Artist { get; set; }

        [JsonPropertyName("album")]
        public DeezerAlbum? Album { get; set; }

        public string? GetCoverUrl()
            => FirstNonEmpty(Album?.CoverBig, Album?.CoverXl, Album?.CoverMedium, Album?.Cover);
    }

    private sealed class DeezerArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class DeezerAlbum
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("cover_medium")]
        public string? CoverMedium { get; set; }

        [JsonPropertyName("cover_big")]
        public string? CoverBig { get; set; }

        [JsonPropertyName("cover_xl")]
        public string? CoverXl { get; set; }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
