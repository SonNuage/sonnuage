using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

public sealed record SimilarArtist(string MusicBrainzId, string Name, string? Comment, double Score);

public sealed record TopRecording(string Title, string? RecordingMbid, double? LengthSeconds, string? ReleaseName);

/// <summary>
/// ListenBrainz (libre, identifiants MusicBrainz) : artistes proches d'un artiste, et ses titres les plus écoutés.
/// Rien n'est envoyé d'autre que l'identifiant MusicBrainz de l'artiste. Une requête par seconde au plus.
/// </summary>
public sealed class ListenBrainzClient(IHttpClientFactory httpFactory, IOptions<SonNuageOptions> options, ILogger<ListenBrainzClient> logger)
{
    // Algorithme public des « artistes similaires » de ListenBrainz (sessions d'écoute sur environ 20 ans).
    private const string SimilarAlgorithm = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    public async Task<IReadOnlyList<SimilarArtist>> SimilarArtistsAsync(string artistMbid, CancellationToken ct)
    {
        var url = $"{options.Value.ListenBrainzLabsUrl.TrimEnd('/')}/similar-artists/json?artist_mbids={Uri.EscapeDataString(artistMbid)}&algorithm={SimilarAlgorithm}";
        return ParseSimilar(await GetAsync(url, ct));
    }

    public async Task<IReadOnlyList<TopRecording>> TopRecordingsAsync(string artistMbid, CancellationToken ct)
    {
        var url = $"{options.Value.ListenBrainzUrl.TrimEnd('/')}/1/popularity/top-recordings-for-artist/{Uri.EscapeDataString(artistMbid)}";
        return ParseTopRecordings(await GetAsync(url, ct));
    }

    internal static IReadOnlyList<SimilarArtist> ParseSimilar(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => Str(a, "artist_mbid") is not null && Str(a, "name") is not null)
            .Select(a => new SimilarArtist(Str(a, "artist_mbid")!, Str(a, "name")!, Str(a, "comment") is { Length: > 0 } c ? c : null, Number(a, "score") ?? 0))
            .ToList();

    internal static IReadOnlyList<TopRecording> ParseTopRecordings(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>()
            .Where(r => Str(r, "recording_name") is not null)
            .Select(r => new TopRecording(Str(r, "recording_name")!, Str(r, "recording_mbid"),
                Number(r, "length") is { } ms and > 0 ? ms / 1000 : null, Str(r, "release_name")))
            .ToList();

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + TimeSpan.FromSeconds(1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastCall = DateTime.UtcNow;
            var http = httpFactory.CreateClient(MusicBrainzClient.HttpClientName);
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("ListenBrainz {Status} pour {Url}", (int)response.StatusCode, url);
                return "[]";
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? Number(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;
}
