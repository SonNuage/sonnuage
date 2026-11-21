using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

/// <summary>
/// API publique de Deezer (sans compte ni clé) : titres phares d'un artiste, quand ListenBrainz ne les donne pas
/// (son point d'accès exige désormais un jeton). Seul le nom de l'artiste est envoyé.
/// </summary>
public sealed class DeezerClient(IHttpClientFactory httpFactory, IOptions<SonNuageOptions> options, ILogger<DeezerClient> logger)
{
    private const int TopLimit = 10;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    /// <summary>Titres phares de l'artiste portant exactement ce nom (aucun si Deezer ne le connaît pas).</summary>
    public async Task<IReadOnlyList<TopRecording>> TopTracksAsync(string artistName, CancellationToken ct)
    {
        var baseUrl = options.Value.DeezerUrl.TrimEnd('/');
        var id = ParseArtistId(await GetAsync($"{baseUrl}/search/artist?q={Uri.EscapeDataString(artistName)}&limit=10", ct), artistName);
        return id is { } artistId ? ParseTop(await GetAsync($"{baseUrl}/artist/{artistId}/top?limit={TopLimit}", ct), artistId) : [];
    }

    /// <summary>Titres d'un album de l'artiste, dans l'ordre, chacun avec son artiste (aucun si Deezer ne le trouve pas).</summary>
    public async Task<IReadOnlyList<TopRecording>> AlbumTracksAsync(string artist, string album, CancellationToken ct)
    {
        var baseUrl = options.Value.DeezerUrl.TrimEnd('/');
        var precise = $"artist:\"{artist}\" album:\"{album}\"";
        var id = ParseAlbumId(await GetAsync($"{baseUrl}/search/album?q={Uri.EscapeDataString(precise)}&limit=25", ct), artist, album)
            ?? ParseAlbumId(await GetAsync($"{baseUrl}/search/album?q={Uri.EscapeDataString($"{artist} {album}")}&limit=25", ct), artist, album);
        return id is { } albumId ? ParseAlbumTracks(await GetAsync($"{baseUrl}/album/{albumId}/tracks?limit=100", ct)) : [];
    }

    /// <summary>Titres, albums et artistes du catalogue pour un texte libre (présence dans la bibliothèque non renseignée).</summary>
    public async Task<CatalogSearchDto> SearchAsync(string text, int limit, CancellationToken ct)
    {
        var baseUrl = options.Value.DeezerUrl.TrimEnd('/');
        var q = Uri.EscapeDataString(text);
        var tracks = ParseCatalogTracks(await GetAsync($"{baseUrl}/search/track?q={q}&limit={limit}", ct));
        var albums = ParseCatalogAlbums(await GetAsync($"{baseUrl}/search/album?q={q}&limit={limit}", ct), null);
        var artists = ParseCatalogArtists(await GetAsync($"{baseUrl}/search/artist?q={q}&limit={limit}", ct));
        return new CatalogSearchDto(tracks, albums, artists);
    }

    /// <summary>Discographie d'un artiste Deezer, la plus récente d'abord.</summary>
    public async Task<IReadOnlyList<CatalogAlbumDto>> ArtistAlbumsAsync(long artistId, string artistName, CancellationToken ct)
    {
        var baseUrl = options.Value.DeezerUrl.TrimEnd('/');
        return ParseCatalogAlbums(await GetAsync($"{baseUrl}/artist/{artistId}/albums?limit=100", ct), artistName)
            .OrderByDescending(a => a.Year).ToList();
    }

    internal static IReadOnlyList<CatalogTrackDto> ParseCatalogTracks(string json) =>
        Data(json)
            .Select(t => (Track: t, Title: Str(t, "title_short") is { Length: > 0 } s ? s : Str(t, "title"),
                Artist: t["artist"] is JsonObject a ? Str(a, "name") : null, Album: t["album"] as JsonObject))
            .Where(t => t.Title is { Length: > 0 } && t.Artist is { Length: > 0 })
            .Select(t => new CatalogTrackDto(t.Artist!, t.Title!, t.Album is { } al ? Str(al, "title") : null, Positive(t.Track, "duration"),
                t.Album is { } cover ? Image(cover, "cover_medium") : null, false))
            .ToList();

    /// <param name="artistName">Artiste d'une discographie (ses albums ne le répètent pas) ; null pour une recherche.</param>
    internal static IReadOnlyList<CatalogAlbumDto> ParseCatalogAlbums(string json, string? artistName) =>
        Data(json)
            .Select(a => (Album: a, Id: Long(a, "id"), Title: Str(a, "title"), Artist: artistName ?? (a["artist"] is JsonObject ar ? Str(ar, "name") : null)))
            .Where(a => a.Id is not null && a.Title is { Length: > 0 } && a.Artist is { Length: > 0 })
            .Select(a => new CatalogAlbumDto(a.Id!.Value, a.Artist!, a.Title!,
                Str(a.Album, "release_date") is { Length: >= 4 } date ? date[..4] : null, Str(a.Album, "record_type"),
                Positive(a.Album, "nb_tracks") is { } n ? (int)n : null, Image(a.Album, "cover_medium"), false))
            .ToList();

    internal static IReadOnlyList<CatalogArtistDto> ParseCatalogArtists(string json) =>
        Data(json)
            .Where(a => Long(a, "id") is not null && Str(a, "name") is { Length: > 0 })
            .Select(a => new CatalogArtistDto(Long(a, "id")!.Value, Str(a, "name")!, Positive(a, "nb_album") is { } n ? (int)n : null,
                Image(a, "picture_medium"), false))
            .ToList();

    private static double? Positive(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.GetValue<double>() > 0 ? v.GetValue<double>() : null;

    // Seules les images de Deezer (autorisées par la politique de contenu des pages) sont gardées.
    private static string? Image(JsonObject o, string key) =>
        Str(o, key) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase) ? url : null;

    /// <summary>L'album de l'artiste portant ce titre, sinon une de ses éditions (Deluxe, Remastered…).</summary>
    internal static long? ParseAlbumId(string json, string artist, string album)
    {
        var artistKey = Library.NameNormalizer.Key(artist);
        var albumKey = AlbumKey(album);
        var candidates = Data(json)
            .Where(a => a["artist"] is JsonObject ar && Str(ar, "name") is { } name && Library.NameNormalizer.Key(name) == artistKey)
            .Where(a => Long(a, "id") is not null && Str(a, "title") is not null)
            .ToList();
        var exact = candidates.FirstOrDefault(a => AlbumKey(Str(a, "title")!) == albumKey);
        var edition = candidates.FirstOrDefault(a => AlbumKey(WithoutEdition(Str(a, "title")!)) == albumKey);
        return Long(exact ?? edition ?? new JsonObject(), "id");
    }

    internal static IReadOnlyList<TopRecording> ParseAlbumTracks(string json) =>
        Data(json)
            .Select(t => (Track: t, Title: Str(t, "title_short") is { Length: > 0 } s ? s : Str(t, "title")))
            .Where(t => t.Title is { Length: > 0 })
            .Select(t => new TopRecording(t.Title!, null,
                t.Track["duration"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number && d.GetValue<double>() > 0 ? d.GetValue<double>() : null, null,
                t.Track["artist"] is JsonObject a ? Str(a, "name") : null))
            .ToList();

    // Mots du titre, parenthèses comprises : « Discovery (Tribute) » n'est pas « Discovery ».
    private static string AlbumKey(string title) => string.Join(' ', YoutubeMatcher.Words(title));

    // « Discovery (Deluxe Edition) » → « Discovery » ; « Discovery (Tribute) » reste tel quel.
    private static string WithoutEdition(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title,
            @"\s*[\(\[][^\)\]]*\b(deluxe|edition|[ée]dition|remaster(ed)?|expanded|anniversary|bonus|version|special)\b[^\)\]]*[\)\]]",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    /// <summary>Parmi les artistes de ce nom (homonymes), le plus suivi.</summary>
    internal static long? ParseArtistId(string json, string artistName)
    {
        var key = Library.NameNormalizer.Key(artistName);
        return Data(json)
            .Where(a => Str(a, "name") is { } name && Library.NameNormalizer.Key(name) == key && Long(a, "id") is not null)
            .OrderByDescending(a => Long(a, "nb_fan") ?? 0)
            .Select(a => Long(a, "id"))
            .FirstOrDefault();
    }

    /// <summary>Titres phares, ceux de l'artiste d'abord, puis ceux où il n'est qu'invité.</summary>
    internal static IReadOnlyList<TopRecording> ParseTop(string json, long artistId) =>
        Data(json)
            .Select(t => (Track: t, Title: Str(t, "title_short") is { Length: > 0 } s ? s : Str(t, "title")))
            .Where(t => t.Title is { Length: > 0 })
            .OrderBy(t => t.Track["artist"] is JsonObject a && Long(a, "id") == artistId ? 0 : 1)
            .Select(t => new TopRecording(t.Title!, null,
                t.Track["duration"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number && d.GetValue<double>() > 0 ? d.GetValue<double>() : null,
                t.Track["album"] is JsonObject album ? Str(album, "title") : null))
            .ToList();

    private static IEnumerable<JsonObject> Data(string json) =>
        (JsonNode.Parse(json) as JsonObject)?["data"] is JsonArray data ? data.OfType<JsonObject>() : [];

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // Deezer tolère 50 requêtes par 5 s : une toutes les 200 ms reste loin de la limite.
            var wait = _lastCall + TimeSpan.FromMilliseconds(200) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastCall = DateTime.UtcNow;
            var http = httpFactory.CreateClient(MusicBrainzClient.HttpClientName);
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Deezer {Status} pour {Url}", (int)response.StatusCode, url);
                return "{}";
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static long? Long(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<long>() : null;

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
