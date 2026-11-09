using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
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

    /// <summary>Titres d'un album de l'artiste, dans l'ordre (aucun si Deezer ne le trouve pas).</summary>
    public async Task<IReadOnlyList<TopRecording>> AlbumTracksAsync(string artist, string album, CancellationToken ct)
    {
        var baseUrl = options.Value.DeezerUrl.TrimEnd('/');
        var precise = $"artist:\"{artist}\" album:\"{album}\"";
        var id = ParseAlbumId(await GetAsync($"{baseUrl}/search/album?q={Uri.EscapeDataString(precise)}&limit=25", ct), artist, album)
            ?? ParseAlbumId(await GetAsync($"{baseUrl}/search/album?q={Uri.EscapeDataString($"{artist} {album}")}&limit=25", ct), artist, album);
        return id is { } albumId ? ParseAlbumTracks(await GetAsync($"{baseUrl}/album/{albumId}/tracks?limit=100", ct)) : [];
    }

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
                t.Track["duration"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number && d.GetValue<double>() > 0 ? d.GetValue<double>() : null, null))
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
