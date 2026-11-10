using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.ListenBrainz;

/// <summary>ListenBrainz ne répond pas (réseau, erreur du service) : rien n'a pu être vérifié.</summary>
public sealed class ListenBrainzUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Une écoute telle que ListenBrainz la reçoit.</summary>
/// <param name="ListenedAt">Début de l'écoute.</param>
public sealed record Listen(DateTimeOffset ListenedAt, string Artist, string Title, string? Release, Guid? RecordingMbid, Guid? ReleaseMbid,
    double DurationSeconds, int? TrackNumber, string? Client);

public enum SubmitStatus
{
    Accepted,
    /// <summary>Jeton refusé (révoqué sur ListenBrainz).</summary>
    Unauthorized,
    /// <summary>Écoutes refusées telles quelles : les renvoyer n'y changerait rien.</summary>
    Rejected,
    /// <summary>Trop de requêtes, service indisponible ou injoignable : réessayer plus tard.</summary>
    Later,
}

/// <param name="RetryAfter">Attente demandée par ListenBrainz (en-tête <c>X-RateLimit-Reset-In</c>), s'il l'a dite.</param>
/// <param name="Remaining">Requêtes encore permises avant cette attente (<c>X-RateLimit-Remaining</c>).</param>
public sealed record SubmitResult(SubmitStatus Status, TimeSpan? RetryAfter = null, int? Remaining = null);

public sealed record Recommendation(Guid RecordingMbid, double Score);

/// <summary>
/// ListenBrainz pour un compte qui l'a relié (niveau 2) : vérification du jeton, envoi des écoutes, recommandations
/// personnelles. Le jeton ne part que dans l'en-tête <c>Authorization</c> et n'est jamais journalisé.
/// </summary>
public sealed class ListenBrainzApi(IHttpClientFactory httpFactory, IOptions<SonNuageOptions> options)
{
    public const string HttpClientName = "listenbrainz";
    public const string SubmissionClient = "SonNuage";

    public static void ConfigureHttp(IHttpClientBuilder builder) => builder.ConfigureHttpClient(http =>
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{SubmissionClient}/{Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.Timeout = TimeSpan.FromSeconds(20);
    });

    private static readonly string Version = Contracts.AppVersion.Of(typeof(ListenBrainzApi).Assembly);

    private string Url(string path) => $"{options.Value.ListenBrainzUrl.TrimEnd('/')}{path}";

    /// <summary>Le nom ListenBrainz du jeton, ou null s'il n'est pas valide.</summary>
    public async Task<string?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/1/validate-token", token);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) return null;
        if (!response.IsSuccessStatusCode) throw new ListenBrainzUnavailableException($"ListenBrainz {(int)response.StatusCode}");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        return json?["valid"]?.GetValueKind() == JsonValueKind.True && Str(json, "user_name") is { Length: > 0 } name ? name : null;
    }

    public async Task<SubmitResult> SubmitAsync(string token, IReadOnlyList<Listen> listens, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/1/submit-listens", token);
        request.Content = new StringContent(Payload(listens).ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(request, ct);
        }
        catch (ListenBrainzUnavailableException)
        {
            return new SubmitResult(SubmitStatus.Later);
        }
        using (response)
        {
            var retry = Header(response, "X-RateLimit-Reset-In") is { } s ? TimeSpan.FromSeconds(Math.Max(0, s)) : (TimeSpan?)null;
            var remaining = Header(response, "X-RateLimit-Remaining");
            var status = response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => SubmitStatus.Accepted,
                HttpStatusCode.Unauthorized => SubmitStatus.Unauthorized,
                HttpStatusCode.BadRequest => SubmitStatus.Rejected,
                _ => SubmitStatus.Later,
            };
            return new SubmitResult(status, retry, remaining);
        }
    }

    /// <summary>Recommandations (filtrage collaboratif) d'un utilisateur ListenBrainz, de la plus forte à la plus faible.</summary>
    public async Task<IReadOnlyList<Recommendation>> RecommendationsAsync(string userName, int count, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/1/cf/recommendation/user/{Uri.EscapeDataString(userName)}/recording?count={count}", null);
        using var response = await SendAsync(request, ct);
        // 204 : pas encore de recommandations (trop peu d'écoutes, ou pas encore calculées).
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return [];
        if (!response.IsSuccessStatusCode) throw new ListenBrainzUnavailableException($"ListenBrainz {(int)response.StatusCode}");
        return ParseRecommendations(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Titre, artiste et album d'enregistrements MusicBrainz.</summary>
    public async Task<IReadOnlyDictionary<Guid, ExternalRecording>> RecordingsAsync(IReadOnlyList<Guid> mbids, CancellationToken ct)
    {
        if (mbids.Count == 0) return new Dictionary<Guid, ExternalRecording>();
        var ids = string.Join(',', mbids);
        using var request = Request(HttpMethod.Get, $"/1/metadata/recording/?recording_mbids={ids}&inc=artist%20release", null);
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ListenBrainzUnavailableException($"ListenBrainz {(int)response.StatusCode}");
        return ParseRecordings(await response.Content.ReadAsStringAsync(ct));
    }

    internal static IReadOnlyList<Recommendation> ParseRecommendations(string json) =>
        ((JsonNode.Parse(json) as JsonObject)?["payload"]?["mbids"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => (Id: Guid.TryParse(Str(r, "recording_mbid"), out var id) ? id : Guid.Empty, Score: Number(r, "score") ?? 0))
            .Where(r => r.Id != Guid.Empty)
            .Select(r => new Recommendation(r.Id, r.Score))
            .ToList();

    internal static IReadOnlyDictionary<Guid, ExternalRecording> ParseRecordings(string json)
    {
        var result = new Dictionary<Guid, ExternalRecording>();
        if (JsonNode.Parse(json) is not JsonObject root) return result;
        foreach (var (key, value) in root)
        {
            if (!Guid.TryParse(key, out var id) || value is not JsonObject o) continue;
            var title = o["recording"] is JsonObject rec ? Str(rec, "name") : null;
            var artist = o["artist"] is JsonObject art ? Str(art, "name") : null;
            if (title is null || artist is null) continue;
            result[id] = new ExternalRecording(id, title, artist, o["release"] is JsonObject rel ? Str(rel, "name") : null);
        }
        return result;
    }

    internal static JsonObject Payload(IReadOnlyList<Listen> listens) => new()
    {
        // « single » : une écoute qui vient de se terminer ; « import » : plusieurs à la fois (file en retard).
        ["listen_type"] = listens.Count == 1 ? "single" : "import",
        ["payload"] = new JsonArray(listens.Select(l => (JsonNode)new JsonObject
        {
            ["listened_at"] = l.ListenedAt.ToUnixTimeSeconds(),
            ["track_metadata"] = Metadata(l),
        }).ToArray()),
    };

    private static JsonObject Metadata(Listen l)
    {
        var info = new JsonObject { ["submission_client"] = SubmissionClient, ["submission_client_version"] = Version };
        if (l.RecordingMbid is { } rec) info["recording_mbid"] = rec.ToString();
        if (l.ReleaseMbid is { } rel) info["release_mbid"] = rel.ToString();
        if (l.DurationSeconds > 0) info["duration_ms"] = (long)Math.Round(l.DurationSeconds * 1000);
        if (l.TrackNumber is { } n) info["tracknumber"] = n;
        if (l.Client is { } client) info["media_player"] = client;
        var metadata = new JsonObject { ["artist_name"] = l.Artist, ["track_name"] = l.Title };
        if (!string.IsNullOrWhiteSpace(l.Release)) metadata["release_name"] = l.Release;
        metadata["additional_info"] = info;
        return metadata;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ListenBrainzUnavailableException("ListenBrainz injoignable", ex);
        }
    }

    private static int? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? Number(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;
}

public sealed record ExternalRecording(Guid RecordingMbid, string Title, string Artist, string? Release);
