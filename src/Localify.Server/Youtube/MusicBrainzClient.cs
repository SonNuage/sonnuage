using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Localify.Server.Youtube;

public sealed record MbRelease(
    string Id,
    string Title,
    string? ArtistCredit,
    string? Status,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? Date,
    string? ReleaseGroupId,
    int? TrackNumber,
    int? DiscNumber);

public sealed record MbRecording(
    string Id,
    string Title,
    string ArtistCredit,
    double? LengthSeconds,
    int SearchScore,
    string? FirstReleaseDate,
    string? TopTag,
    IReadOnlyList<MbRelease> Releases);

/// <summary>
/// Base MusicBrainz (recherche de titres), Cover Art Archive (pochettes) et AcoustID (empreinte acoustique).
/// MusicBrainz impose au plus une requête par seconde et un User-Agent identifiable.
/// </summary>
public sealed class MusicBrainzClient(IHttpClientFactory httpFactory, YoutubeTools tools, ILogger<MusicBrainzClient> logger)
{
    public const string HttpClientName = "musicbrainz";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    public static void ConfigureHttp(IHttpClientBuilder builder) => builder.ConfigureHttpClient((sp, http) =>
    {
        var contact = sp.GetRequiredService<IOptions<LocalifyOptions>>().Value.MusicBrainzContact;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Localify/1.0 (+{contact})");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.Timeout = TimeSpan.FromSeconds(20);
    });

    public async Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(string title, string? artist, CancellationToken ct)
    {
        var query = new StringBuilder($"recording:\"{Escape(title)}\"");
        if (!string.IsNullOrWhiteSpace(artist)) query.Append($" AND artist:\"{Escape(artist)}\"");
        return await SearchAsync(query.ToString(), 15, ct);
    }

    public async Task<IReadOnlyList<MbRecording>> RecordingsByIdAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await SearchAsync(string.Join(" OR ", ids.Take(10).Select(id => $"rid:{id}")), 10, ct);

    private async Task<IReadOnlyList<MbRecording>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = $"https://musicbrainz.org/ws/2/recording?fmt=json&limit={limit}&query={Uri.EscapeDataString(query)}";
        var json = await GetJsonAsync(url, ct);
        return (json?["recordings"]?.AsArray() ?? []).OfType<JsonObject>().Select(ParseRecording).ToList();
    }

    private async Task<JsonObject?> GetJsonAsync(string url, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            await Gate.WaitAsync(ct);
            try
            {
                var wait = _lastCall + TimeSpan.FromSeconds(1.1) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastCall = DateTime.UtcNow;
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    _lastCall = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    /// <summary>Pochette de face 500 px : celle de l'édition, sinon celle du groupe de releases.</summary>
    public async Task<byte[]?> FrontCoverAsync(string? releaseId, string? releaseGroupId, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        foreach (var url in new[]
                 {
                     releaseId is null ? null : $"https://coverartarchive.org/release/{releaseId}/front-500",
                     releaseGroupId is null ? null : $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500",
                 })
        {
            if (url is null) continue;
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug("Cover Art Archive : {Message}", ex.Message);
            }
        }
        return null;
    }

    /// <summary>Identifiants MusicBrainz reconnus par AcoustID d'après l'empreinte du fichier (clé d'API gratuite requise).</summary>
    public async Task<IReadOnlyList<(string RecordingId, double Score)>> AcoustIdAsync(string audioPath, string apiKey, CancellationToken ct)
    {
        if (tools.Fpcalc is not { } fpcalc) return [];
        var start = new ProcessStartInfo(fpcalc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-json");
        start.ArgumentList.Add(audioPath);
        using var process = Process.Start(start);
        if (process is null) return [];
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) return [];
        var fp = JsonNode.Parse(output)!.AsObject();

        var http = httpFactory.CreateClient(HttpClientName);
        using var response = await http.PostAsync("https://api.acoustid.org/v2/lookup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = apiKey,
            ["meta"] = "recordingids",
            ["duration"] = ((int)fp["duration"]!.GetValue<double>()).ToString(),
            ["fingerprint"] = fp["fingerprint"]!.GetValue<string>(),
        }), ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
        if (json?["status"]?.GetValue<string>() != "ok")
        {
            logger.LogWarning("AcoustID : {Error}", json?["error"]?["message"]?.GetValue<string>() ?? response.StatusCode.ToString());
            return [];
        }
        return (json["results"]?.AsArray() ?? []).OfType<JsonObject>()
            .SelectMany(r => (r["recordings"]?.AsArray() ?? []).OfType<JsonObject>()
                .Select(rec => (rec["id"]!.GetValue<string>(), r["score"]!.GetValue<double>())))
            .DistinctBy(x => x.Item1)
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    internal static MbRecording ParseRecording(JsonObject r)
    {
        var releases = (r["releases"]?.AsArray() ?? []).OfType<JsonObject>().Select(rel =>
        {
            var group = rel["release-group"] as JsonObject;
            var medium = (rel["media"]?.AsArray() ?? []).OfType<JsonObject>().FirstOrDefault();
            var track = (medium?["track"]?.AsArray() ?? medium?["tracks"]?.AsArray() ?? []).OfType<JsonObject>().FirstOrDefault();
            return new MbRelease(
                Str(rel, "id")!,
                Str(rel, "title") ?? "",
                rel["artist-credit"] is JsonArray credit ? Credit(credit) : null,
                Str(rel, "status"),
                group is null ? null : Str(group, "primary-type"),
                (group?["secondary-types"]?.AsArray() ?? []).Select(t => t?.GetValue<string>()).OfType<string>().ToList(),
                Str(rel, "date"),
                group is null ? null : Str(group, "id"),
                int.TryParse(track is null ? null : Str(track, "number"), out var n) ? n : Int(track, "position"),
                Int(medium, "position"));
        }).ToList();
        var tags = (r["tags"]?.AsArray() ?? []).OfType<JsonObject>().OrderByDescending(t => Int(t, "count") ?? 0).FirstOrDefault();
        return new MbRecording(
            Str(r, "id")!,
            Str(r, "title") ?? "",
            r["artist-credit"] is JsonArray c ? Credit(c) : "",
            r["length"] is JsonValue l && l.GetValueKind() == JsonValueKind.Number ? l.GetValue<double>() / 1000 : null,
            Int(r, "score") ?? 0,
            Str(r, "first-release-date"),
            tags is null ? null : Str(tags, "name"),
            releases);
    }

    /// <summary>« Daft Punk feat. Pharrell Williams » à partir des crédits (nom + phrase de liaison).</summary>
    private static string Credit(JsonArray credit) =>
        string.Concat(credit.OfType<JsonObject>().Select(c => (Str(c, "name") ?? "") + (Str(c, "joinphrase") ?? ""))).Trim();

    private static string? Str(JsonObject? o, string key) => o is null ? null : YtDlp.Str(o, key);

    private static int? Int(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<int>() : null;

    // Caractères spéciaux de la syntaxe Lucene de MusicBrainz.
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if ("+-&|!(){}[]^\"~*?:\\/".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
