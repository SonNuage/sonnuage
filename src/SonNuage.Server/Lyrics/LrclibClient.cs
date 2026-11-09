using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Lyrics;

/// <summary>Ce qu'on sait du morceau pour lui trouver des paroles.</summary>
public sealed record LyricsQuery(string Title, string Artist, string Album, double DurationSeconds);

/// <summary>Paroles trouvées sur LRCLIB : LRC daté et/ou texte brut. <see cref="None"/> : rien pour ce morceau.</summary>
public sealed record LrclibLyrics(string? Synced, string? Plain)
{
    public static readonly LrclibLyrics None = new(null, null);
}

/// <summary>
/// LRCLIB (base libre de paroles, sans compte) : recherche exacte par titre, artiste, album et durée, puis repli sur
/// la recherche libre au plus près de la durée. Une requête à la fois, avec un User-Agent identifiable comme demandé.
/// Une erreur du service lève <see cref="HttpRequestException"/> : ce n'est pas une absence de paroles.
/// </summary>
public sealed class LrclibClient(IHttpClientFactory httpFactory, IOptions<SonNuageOptions> options, ILogger<LrclibClient> logger)
{
    public const string HttpClientName = "lrclib";
    private const string UserAgent = "SonNuage (https://git.pmjb.org)";
    /// <summary>Écart de durée toléré entre le morceau et une réponse de la recherche libre.</summary>
    private const double DurationTolerance = 3;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<LrclibLyrics> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        var baseUrl = options.Value.LrclibUrl.TrimEnd('/');
        var exact = await GetAsync($"{baseUrl}/api/get?artist_name={E(query.Artist)}&track_name={E(query.Title)}&album_name={E(query.Album)}"
            + $"&duration={Math.Round(query.DurationSeconds).ToString(CultureInfo.InvariantCulture)}", ct);
        if (exact is not null) return Lyrics(JsonNode.Parse(exact) as JsonObject);
        var search = await GetAsync($"{baseUrl}/api/search?track_name={E(query.Title)}&artist_name={E(query.Artist)}", ct);
        return search is null ? LrclibLyrics.None : PickBest(search, query.DurationSeconds);
    }

    private static string E(string value) => Uri.EscapeDataString(value);

    /// <summary>Parmi les réponses assez proches en durée : celles qui sont datées d'abord, puis la plus proche.</summary>
    internal static LrclibLyrics PickBest(string json, double durationSeconds) =>
        (JsonNode.Parse(json) as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(o => (Lyrics: Lyrics(o), Gap: Number(o, "duration") is { } d ? Math.Abs(d - durationSeconds) : double.MaxValue))
            .Where(c => c.Lyrics != LrclibLyrics.None && c.Gap <= DurationTolerance)
            .OrderBy(c => c.Lyrics.Synced is null)
            .ThenBy(c => c.Gap)
            .Select(c => c.Lyrics)
            .FirstOrDefault() ?? LrclibLyrics.None;

    private static LrclibLyrics Lyrics(JsonObject? o) =>
        o is null || o["instrumental"] is JsonValue v && v.GetValueKind() == JsonValueKind.True
            ? LrclibLyrics.None
            : new LrclibLyrics(Text(o, "syncedLyrics"), Text(o, "plainLyrics"));

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static double? Number(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    /// <summary>Corps de la réponse, null pour un 404 (rien de connu).</summary>
    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("LRCLIB {Status} pour {Url}", (int)response.StatusCode, url);
                response.EnsureSuccessStatusCode();
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
