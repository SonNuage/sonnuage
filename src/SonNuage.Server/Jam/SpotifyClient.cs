using System.Net.Http.Headers;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Jam;

/// <summary>Jeton d'accès rafraîchi : valeur et nouvelle expiration (et le jeton de rafraîchissement s'il a changé).</summary>
public sealed record SpotifyRefresh(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken);

/// <summary>Hôtes d'API Spotify résolus dynamiquement (apresolve) : façade « spclient » et aiguilleur « dealer ».</summary>
public sealed record SpotifyHosts(string SpClient, string Dealer);

/// <summary>Un membre d'un Jam.</summary>
public sealed record JamMember(string Id, string DisplayName, bool IsOwner);

/// <summary>Informations d'un Jam lues avant de le rejoindre.</summary>
public sealed record JamSessionInfo(string SessionId, string JoinToken, string? HostName, bool Active, IReadOnlyList<JamMember> Members);

/// <summary>
/// Accès bas niveau à Spotify pour suivre un Jam : rafraîchissement du jeton, métadonnées d'un titre (API Web
/// publique), et endpoints internes « social-connect » / « connect-state » (façade spclient). Le son n'est jamais
/// demandé. Le client id est celui de l'appli de bureau, seul accepté par social-connect — hors des CGU de Spotify,
/// la fonctionnalité est donc expérimentale et peut cesser de marcher si Spotify change son API.
/// </summary>
public sealed class SpotifyClient(HttpClient http, ILogger<SpotifyClient> log)
{
    /// <summary>Client id public de l'appli de bureau Spotify (comme librespot), seul reconnu par social-connect.</summary>
    public const string ClientId = "65b708073fc0480ea92a077233ca87bd";

    /// <summary>Client HTTP sous-jacent, pour résoudre un lien court de partage (<see cref="SpotifyLink"/>).</summary>
    public HttpClient Http => http;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Échange un jeton de rafraîchissement contre un jeton d'accès frais.</summary>
    public async Task<SpotifyRefresh?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        });
        using var res = await http.PostAsync("https://accounts.spotify.com/api/token", body, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Rafraîchissement du jeton Spotify refusé : {Status}", (int)res.StatusCode);
            return null;
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at)) return null;
        var expires = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        return new SpotifyRefresh(at.GetString()!, DateTimeOffset.UtcNow.AddSeconds(expires), newRefresh);
    }

    /// <summary>Nom affiché et identifiant du compte (<c>/v1/me</c>), pour confirmer la liaison.</summary>
    public async Task<string?> DisplayNameAsync(string accessToken, CancellationToken ct)
    {
        using var req = Get("https://api.spotify.com/v1/me", accessToken);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        return doc.RootElement.TryGetProperty("display_name", out var n) ? n.GetString() : null;
    }

    /// <summary>Hôtes spclient + dealer à utiliser pour ce compte.</summary>
    public async Task<SpotifyHosts?> ResolveHostsAsync(CancellationToken ct)
    {
        using var res = await http.GetAsync("https://apresolve.spotify.com/?type=spclient&type=dealer", ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        var spclient = First(root, "spclient");
        var dealer = First(root, "dealer");
        if (spclient is null || dealer is null) return null;
        return new SpotifyHosts($"https://{Strip(spclient)}", Strip(dealer));

        static string? First(JsonElement root, string key) =>
            root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 ? a[0].GetString() : null;
        static string Strip(string host) => host.EndsWith(":443", StringComparison.Ordinal) ? host[..^4] : host;
    }

    /// <summary>
    /// Métadonnées propres d'un titre (titre, artistes) via la page d'intégration publique
    /// <c>open.spotify.com/embed/track/{id}</c> — sans authentification, et sans la limite de débit sévère de l'API Web
    /// sur ce client id. L'album et la durée, quand ils manquent ici, viennent du cluster. Pas d'ISRC.
    /// </summary>
    public async Task<JamTrackDto?> TrackAsync(string accessToken, string trackId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"https://open.spotify.com/embed/track/{Uri.EscapeDataString(trackId)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        var html = await res.Content.ReadAsStringAsync(ct);
        var start = html.IndexOf("__NEXT_DATA__", StringComparison.Ordinal);
        if (start < 0) return null;
        start = html.IndexOf('>', start);
        var end = start < 0 ? -1 : html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(html[(start + 1)..end]);
            if (FindEntity(doc.RootElement) is not { } entity) return null;
            var title = entity.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var artist = entity.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? string.Join(", ", ar.EnumerateArray().Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null).Where(s => !string.IsNullOrEmpty(s)))
                : "";
            if (string.IsNullOrEmpty(title)) return null;
            var duration = entity.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() / 1000.0 : 0;
            return new JamTrackDto(title, artist, null, duration, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Repère l'objet du titre dans le __NEXT_DATA__ de la page d'intégration : un objet avec « name » et « artists ».</summary>
    private static JsonElement? FindEntity(JsonElement node, int depth = 0)
    {
        if (depth > 12) return null;
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("name", out _) && node.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array) return node;
                foreach (var p in node.EnumerateObject())
                    if (FindEntity(p.Value, depth + 1) is { } found) return found;
                return null;
            case JsonValueKind.Array:
                foreach (var e in node.EnumerateArray())
                    if (FindEntity(e, depth + 1) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    /// <summary>Informations du Jam désigné par un jeton de partage, ou null s'il n'existe pas / plus.</summary>
    public async Task<JamSessionInfo?> SessionInfoAsync(SpotifyHosts hosts, string accessToken, string joinToken, CancellationToken ct)
    {
        using var req = Get($"{hosts.SpClient}/social-connect/v2/sessions/info/{Uri.EscapeDataString(joinToken)}", accessToken);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        var sessionId = Text(root, "session_id") ?? joinToken;
        var token = Text(root, "join_session_token") ?? joinToken;
        var hostName = root.TryGetProperty("host_device_info", out var hi) && hi.ValueKind == JsonValueKind.Object ? Text(hi, "device_name") : null;
        var active = root.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.True;
        var ownerId = Text(root, "session_owner_id");
        var members = new List<JamMember>();
        if (root.TryGetProperty("session_members", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
            {
                var id = Text(m, "id") ?? "";
                members.Add(new JamMember(id, Text(m, "display_name") ?? "", id == ownerId));
            }
        // Le nom du compte hôte (« LOG_Drakon ») parle plus que celui de son appareil ; l'appareil en secours.
        if (members.FirstOrDefault(m => m.IsOwner)?.DisplayName is { Length: > 0 } owner) hostName = owner;
        return new JamSessionInfo(sessionId, token, hostName, active, members);
    }

    /// <summary>Rejoint le Jam en écoute + contrôle. Renvoie vrai si accepté (200), faux sinon (403 non autorisé…).</summary>
    public async Task<bool> JoinAsync(SpotifyHosts hosts, string accessToken, string joinToken, string localDeviceId, CancellationToken ct)
    {
        // « control » = écouter sur l'appareil de l'hôte (permis aux comptes gratuits) ; « listen_and_control » exige Premium.
        foreach (var mode in new[] { "control", "listen_and_control" })
        {
            var url = $"{hosts.SpClient}/social-connect/v2/sessions/join/{Uri.EscapeDataString(joinToken)}" +
                $"?playback_control={mode}&join_type=deeplinking&local_device_id={Uri.EscapeDataString(localDeviceId)}" +
                $"&join_attempt_id={Guid.NewGuid():N}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) { log.LogInformation("Jam rejoint ({Mode})", mode); return true; }
            log.LogWarning("Rejoindre le Jam refusé ({Mode}) : {Status} {Body}", mode, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        return false;
    }

    /// <summary>
    /// Quitte le Jam côté Spotify (comme « Quitter » dans l'appli), pour ne pas y rester membre fantôme. Rend vrai si
    /// Spotify l'a accepté.
    /// </summary>
    public async Task<bool> LeaveAsync(SpotifyHosts hosts, string accessToken, string sessionId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{hosts.SpClient}/social-connect/v3/sessions/{Uri.EscapeDataString(sessionId)}/leave");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        log.LogInformation("Jam quitté : {Status}", (int)res.StatusCode);
        return res.IsSuccessStatusCode;
    }

    /// <summary>
    /// Enregistre un appareil caché et non lecteur (connect-state) avec l'identifiant de connexion du dealer, et renvoie
    /// l'état de lecture courant du compte — c'est-à-dire, une fois le Jam rejoint, celui de l'hôte.
    /// </summary>
    public async Task<ClusterState?> RegisterReceiverAsync(SpotifyHosts hosts, string accessToken, string deviceId, string connectionId, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            member_type = "CONNECT_STATE",
            device = new { device_info = new { capabilities = new { can_be_player = false, hidden = true, needs_full_player_state = true } } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{hosts.SpClient}/connect-state/v1/devices/hobs_{deviceId}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("X-Spotify-Connection-Id", connectionId);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Enregistrement connect-state refusé : {Status}", (int)res.StatusCode);
            return null;
        }
        return ClusterState.Parse(await res.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>Chaîne JSON, ou null si absente ou d'un autre type : un champ qui change de forme ne casse pas le suivi.</summary>
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static HttpRequestMessage Get(string url, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }
}
