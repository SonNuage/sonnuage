using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Lecture directe depuis YouTube, côté appareil, pour démarrer un titre absent de la bibliothèque en moins d'une
/// seconde (Jam : l'hôte change de titre à la volée). Passe par l'API interne InnerTube, comme les applis officielles :
/// <list type="number">
/// <item>un <c>visitorData</c> (identifiant de visiteur anonyme, lu dans <c>sw.js_data</c>, gardé ~30 min) ;</item>
/// <item>une recherche YouTube Music filtrée « Titres » (versions studio, avec leur durée, comparée à celle de Spotify) ;</item>
/// <item>le lecteur du client visionOS, qui donne des URL audio en clair (ni jeton anti-robot, ni JS à exécuter).</item>
/// </list>
/// Fragile par nature (YouTube ferme ce genre d'accès sans prévenir) : tout échec rend null et l'appelant se rabat
/// sur l'import classique du serveur. Mesuré le 18/11/2026 : ~0,4–0,7 s de la recherche aux premiers octets.
/// </summary>
public sealed class YoutubeDirect
{
    private const string WebUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    private const string VisionUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    /// <summary>Filtre « Titres » de la recherche YouTube Music.</summary>
    private const string SongsFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private static readonly TimeSpan VisitorLifetime = TimeSpan.FromMinutes(30);
    /// <summary>Les URL googlevideo expirent au bout de 6 h ; on les garde bien moins longtemps.</summary>
    private static readonly TimeSpan AudioLifetime = TimeSpan.FromHours(4);

    /// <summary>Uniquement dans l'appli : le navigateur n'a pas de SocketsHttpHandler et YouTube y refuse le CORS.</summary>
    [UnsupportedOSPlatformGuard("browser")]
    public static bool Supported => !OperatingSystem.IsBrowser();

    private readonly HttpClient _http = Supported
        ? new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestVersion = HttpVersion.Version20,
        }
        : new();

    private readonly SemaphoreSlim _visitorLock = new(1, 1);
    private string? _visitor;
    private DateTimeOffset _visitorAt;
    private readonly Dictionary<string, (YoutubeAudio Audio, DateTimeOffset At)> _audio = new();

    /// <summary>Prépare la session (visiteur + connexions TLS) pour que le premier vrai appel soit rapide.</summary>
    public async Task WarmUpAsync()
    {
        if (!Supported) return;
        try
        {
            await VisitorAsync(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
        }
    }

    /// <summary>Trouve le titre sur YouTube Music et rend son flux audio, ou null.</summary>
    public async Task<YoutubeAudio?> FindAsync(string title, string artist, double durationSeconds, CancellationToken ct = default)
    {
        if (!Supported) return null;
        try
        {
            var videoId = await SearchAsync($"{artist} {title}".Trim(), durationSeconds, ct);
            return videoId is null ? null : await AudioAsync(videoId, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return null;
        }
    }

    /// <summary>Flux audio d'une vidéo connue (URL directe en cache quelques heures), ou null.</summary>
    public async Task<YoutubeAudio?> AudioAsync(string videoId, CancellationToken ct = default)
    {
        if (!Supported) return null;
        lock (_audio)
            if (_audio.TryGetValue(videoId, out var hit) && DateTimeOffset.UtcNow - hit.At < AudioLifetime) return hit.Audio;
        try
        {
            var audio = await PlayerAsync(videoId, await VisitorAsync(false, ct), ct)
                // Visiteur expiré ou refusé (« confirmez que vous n'êtes pas un robot ») : un neuf, une seule fois.
                ?? await PlayerAsync(videoId, await VisitorAsync(true, ct), ct);
            if (audio is not null)
                lock (_audio) _audio[videoId] = (audio, DateTimeOffset.UtcNow);
            return audio;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// visitorData de la réponse de <c>sw.js_data</c> : préfixe « )]}' » (anti-JSON hijacking), puis un tableau où il
    /// est en [0][2][0][0][13]. Null si la réponse n'a pas cette forme (page de consentement, erreur, format changé).
    /// </summary>
    public static string? ParseVisitor(string text)
    {
        var start = text.IndexOf('[');
        if (start < 0) return null;
        try
        {
            var node = JsonNode.Parse(text[start..])?[0]?[2]?[0]?[0]?[13];
            return node is JsonValue value && value.TryGetValue<string>(out var visitor) && visitor.Length > 0 ? visitor : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task<string> VisitorAsync(bool renew, CancellationToken ct = default)
    {
        if (!renew && _visitor is { } v && DateTimeOffset.UtcNow - _visitorAt < VisitorLifetime) return v;
        await _visitorLock.WaitAsync(ct);
        try
        {
            if (!renew && _visitor is { } again && DateTimeOffset.UtcNow - _visitorAt < VisitorLifetime) return again;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
            req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var text = await res.Content.ReadAsStringAsync(ct);
            var visitor = ParseVisitor(text) ?? throw new HttpRequestException("visitorData introuvable");
            _visitor = visitor;
            _visitorAt = DateTimeOffset.UtcNow;
            return visitor;
        }
        finally
        {
            _visitorLock.Release();
        }
    }

    private async Task<string?> SearchAsync(string query, double durationSeconds, CancellationToken ct)
    {
        var visitor = await VisitorAsync(false, ct);
        var body = new JsonObject
        {
            ["context"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["clientName"] = "WEB_REMIX", ["clientVersion"] = "1.20260707.12.00",
                    ["hl"] = "en", ["gl"] = "FR", ["visitorData"] = visitor,
                },
            },
            ["query"] = query,
            ["params"] = Uri.UnescapeDataString(SongsFilter),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search?prettyPrint=false")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
        req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await res.Content.ReadAsStreamAsync(ct));
        return PickSong(root, durationSeconds);
    }

    /// <summary>
    /// Premier résultat « Titre » dont la durée colle à celle de Spotify (±max(6 s, 4 %)), sinon le premier tout court.
    /// </summary>
    public static string? PickSong(JsonNode? root, double durationSeconds)
    {
        var sections = root?["contents"]?["tabbedSearchResultsRenderer"]?["tabs"]?[0]?["tabRenderer"]?["content"]?["sectionListRenderer"]?["contents"]?.AsArray();
        if (sections is null) return null;
        string? first = null;
        var tolerance = Math.Max(6, durationSeconds * 0.04);
        foreach (var section in sections)
        {
            var items = section?["musicShelfRenderer"]?["contents"]?.AsArray();
            if (items is null) continue;
            foreach (var item in items)
            {
                var r = item?["musicResponsiveListItemRenderer"];
                var videoId = r?["playlistItemData"]?["videoId"]?.GetValue<string>();
                if (videoId is null) continue;
                first ??= videoId;
                if (durationSeconds <= 0) return videoId;
                var runs = r?["flexColumns"]?[1]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]?.AsArray();
                var seconds = runs?.Select(x => ParseDuration(x?["text"]?.GetValue<string>())).LastOrDefault(d => d > 0) ?? 0;
                if (seconds > 0 && Math.Abs(seconds - durationSeconds) <= tolerance) return videoId;
            }
        }
        return first;
    }

    /// <summary>« 3:04 » ou « 1:02:03 » en secondes, 0 sinon.</summary>
    public static double ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return 0;
        double total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    private async Task<YoutubeAudio?> PlayerAsync(string videoId, string visitor, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["context"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["clientName"] = "VISIONOS", ["clientVersion"] = "1.02",
                    ["deviceMake"] = "Apple", ["deviceModel"] = "RealityDevice17,1",
                    ["osName"] = "visionOS", ["osVersion"] = "26.5.23O471",
                    ["hl"] = "en", ["gl"] = "FR", ["visitorData"] = visitor,
                },
            },
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", VisionUserAgent);
        req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "101");
        req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "1.02");
        req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return PickAudio(videoId, JsonNode.Parse(await res.Content.ReadAsStreamAsync(ct)));
    }

    /// <summary>Meilleur flux audio seul (opus/webm de préférence, sinon AAC/mp4) avec une URL directe.</summary>
    public static YoutubeAudio? PickAudio(string videoId, JsonNode? player)
    {
        if (player?["playabilityStatus"]?["status"]?.GetValue<string>() != "OK") return null;
        var formats = player["streamingData"]?["adaptiveFormats"]?.AsArray();
        if (formats is null) return null;
        YoutubeAudio? best = null;
        var bestScore = -1L;
        foreach (var f in formats)
        {
            var mime = f?["mimeType"]?.GetValue<string>() ?? "";
            var url = f?["url"]?.GetValue<string>();
            if (!mime.StartsWith("audio/", StringComparison.Ordinal) || url is null) continue;
            var bitrate = f?["bitrate"]?.GetValue<long>() ?? 0;
            // Opus d'abord (meilleur à débit égal), puis le débit.
            var score = (mime.StartsWith("audio/webm", StringComparison.Ordinal) ? 1_000_000_000L : 0) + bitrate;
            if (score <= bestScore) continue;
            bestScore = score;
            long.TryParse(f?["contentLength"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var length);
            long.TryParse(f?["approxDurationMs"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var durationMs);
            best = new YoutubeAudio(videoId, url, mime, length, durationMs / 1000.0);
        }
        return best;
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException or IOException;
}

/// <summary>Flux audio YouTube directement lisible (à lire avec un en-tête Range, sinon YouTube bride le débit).</summary>
public sealed record YoutubeAudio(string VideoId, string Url, string MimeType, long ContentLength, double DurationSeconds);

/// <summary>
/// Titre joué directement depuis YouTube, inconnu du serveur : il voyage comme un <see cref="TrackDto"/> ordinaire
/// (file, notification), marqué par son format <c>youtube:{videoId}</c> et un identifiant dérivé de la vidéo.
/// </summary>
public static class YoutubeTracks
{
    private const string Prefix = "youtube:";

    public static TrackDto Create(string videoId, string title, string artist, double durationSeconds) => new(
        Id: IdFor(videoId), Title: title, ArtistId: Guid.Empty, ArtistName: artist, AlbumId: Guid.Empty, AlbumTitle: "",
        TrackNumber: null, DiscNumber: null, DurationSeconds: durationSeconds, Format: Prefix + videoId, IsLossless: false,
        BitrateKbps: null, HasCover: false, IsFavorite: false);

    public static string? VideoId(TrackDto? track) =>
        track?.Format is { } f && f.StartsWith(Prefix, StringComparison.Ordinal) ? f[Prefix.Length..] : null;

    public static bool IsYoutube(TrackDto? track) => VideoId(track) is not null;

    /// <summary>Identifiant stable par vidéo (pas un titre du serveur : ne jamais l'envoyer à l'API comme tel).</summary>
    public static Guid IdFor(string videoId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Prefix + videoId));
        var g = bytes.AsSpan(0, 16).ToArray();
        g[7] = (byte)((g[7] & 0x0F) | 0x50); // version 5-like, distinct des Guid du serveur
        return new Guid(g);
    }
}
