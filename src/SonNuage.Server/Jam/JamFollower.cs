using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Jam;

/// <summary>
/// Suit un Jam Spotify pour un compte : rejoint la session, ouvre le flux « dealer », lit l'état de lecture de l'hôte
/// (connect-state) et, à chaque changement, résout le titre vers une piste SonNuage puis publie l'état (<see
/// cref="JamStateDto"/>). Une instance par compte qui suit un Jam ; <see cref="DisposeAsync"/> l'arrête.
/// </summary>
public sealed class JamFollower : IAsyncDisposable
{
    private static readonly TimeSpan Keepalive = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImportPoll = TimeSpan.FromSeconds(5);

    private readonly Guid _userId;
    private readonly string _joinToken;
    private readonly JamAudio _audio;
    private readonly IServiceScopeFactory _scopes;
    private readonly Action<Guid, JamStateDto> _publish;
    private readonly Action<JamFollower> _ended;
    private readonly TaskCompletionSource<bool> _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _deviceId = Guid.NewGuid().ToString("N");
    private readonly Task _loop;

    private ClusterState? _lastCluster;
    /// <summary>
    /// Notre horloge moins celle de Spotify, estimée comme NTP : à chaque état reçu, (réception − server_timestamp_ms)
    /// vaut décalage + temps de vol ; le minimum observé est la meilleure estimation (le vol le plus court).
    /// </summary>
    private long? _spotifyClockOffsetMs;
    private string? _lastTrackId;
    private string? _hostName;

    public JamFollower(Guid userId, string joinToken, JamAudio audio, IServiceScopeFactory scopes, Action<Guid, JamStateDto> publish,
        Action<JamFollower> ended, ILogger log)
    {
        _userId = userId;
        _joinToken = joinToken;
        _audio = audio;
        _scopes = scopes;
        _publish = publish;
        _ended = ended;
        _log = log;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public Guid UserId => _userId;
    public JamAudio Audio => _audio;

    /// <summary>Issue du premier « join » : vrai si Spotify nous a acceptés dans le Jam, faux s'il a refusé ou échoué.</summary>
    public Task<bool> Joined => _joined.Task;

    /// <summary>Le dernier état connu (pour un nouvel abonné), ou un état inactif tant que rien n'est reçu.</summary>
    public JamStateDto Current() => Build();

    /// <summary>Comment s'est terminée une connexion au dealer.</summary>
    private enum Outcome { Over, Dropped }

    /// <summary>
    /// Suit le Jam tant qu'il dure : une connexion au dealer, et si elle tombe, une nouvelle (le Jam est rejoint à
    /// chaque fois). Le suivi s'arrête quand le Jam est fini (raison de fin poussée, ou « join » refusé) ou après trop
    /// de connexions ratées d'affilée ; le hub est alors prévenu pour que les appareils le sachent.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Outcome outcome;
                try
                {
                    outcome = await FollowOnceAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Coupure, mais aussi délai dépassé (HttpClient, attente de l'id de connexion) : on se reconnecte.
                    _log.LogWarning(ex, "Jam : connexion au dealer perdue pour le compte {User}", _userId);
                    outcome = Outcome.Dropped;
                }
                if (outcome == Outcome.Over || ct.IsCancellationRequested) break;
                failures = _connected ? 0 : failures + 1;
                _connected = false;
                if (failures >= Reconnects.Length) { _log.LogWarning("Jam : abandon après {Count} connexions ratées", failures); break; }
                await Task.Delay(Reconnects[failures], ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arrêt demandé.
        }
        finally
        {
            _joined.TrySetResult(false);
            if (!ct.IsCancellationRequested) _ended(this);
        }
    }

    /// <summary>Pauses avant de se reconnecter, selon le nombre d'échecs d'affilée (au-delà : abandon).</summary>
    private static readonly TimeSpan[] Reconnects = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Vrai dès qu'une connexion a rejoint le Jam : remet le compte des échecs à zéro.</summary>
    private bool _connected;

    /// <summary>Session rejointe (et où la quitter) : à quitter côté Spotify quand on arrête de suivre.</summary>
    private (SpotifyHosts Hosts, string Id)? _session;

    /// <summary>Une raison de fin est arrivée (Jam supprimé, exclu…) : la connexion en cours est la dernière.</summary>
    private volatile bool _over;

    /// <summary>Une connexion : jeton frais, appareil caché enregistré, Jam rejoint, puis lecture des push jusqu'à la coupure.</summary>
    private async Task<Outcome> FollowOnceAsync(CancellationToken ct)
    {
        var accessToken = await FreshAccessTokenAsync(ct);
        if (accessToken is null) { _log.LogWarning("Jam : aucun jeton Spotify valide pour le compte {User}", _userId); return Outcome.Over; }

        // Un client HTTP par opération réseau (sans garder de scope DB ouvert pendant tout le suivi).
        SpotifyHosts? hosts;
        JamSessionInfo? info;
        using (var scope = _scopes.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
            hosts = await client.ResolveHostsAsync(ct);
            if (hosts is null) { _log.LogWarning("Jam : hôtes Spotify introuvables"); return Outcome.Dropped; }
            info = await client.SessionInfoAsync(hosts, accessToken, _joinToken, ct);
            _hostName = info?.HostName ?? _hostName;
            if (info is not null) _session = (hosts, info.SessionId);
        }

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"wss://{hosts.Dealer}/?access_token={Uri.EscapeDataString(accessToken)}"), ct);
        var connectionId = await ReadConnectionIdAsync(ws, ct);
        if (connectionId is null) { _log.LogWarning("Jam : pas d'identifiant de connexion dealer"); return Outcome.Dropped; }

        using (var scope = _scopes.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
            await client.RegisterReceiverAsync(hosts, accessToken, _deviceId, connectionId, ct);
            // Rejoindre APRÈS l'enregistrement, avec l'id connect réel (hobs_…) : les mises à jour du Jam visent l'appareil
            // membre. Refusé : le Jam est fini (ou on n'y a pas droit), le dealer n'enverrait plus rien.
            if (!await client.JoinAsync(hosts, accessToken, info?.JoinToken ?? _joinToken, $"hobs_{_deviceId}", ct))
            {
                _joined.TrySetResult(false);
                return Outcome.Over;
            }
            _joined.TrySetResult(true);
            _connected = true;
            if (await client.RegisterReceiverAsync(hosts, accessToken, _deviceId, connectionId, ct) is { } initial)
                await ProcessAsync(accessToken, initial, ct);
        }

        await PumpAsync(ws, accessToken, ct);
        return _over ? Outcome.Over : Outcome.Dropped;
    }

    /// <summary>Lit le flux dealer : traite « message » (cluster) et « request » (commandes, auxquelles on répond), avec keepalive.</summary>
    private async Task PumpAsync(ClientWebSocket ws, string accessToken, CancellationToken ct)
    {
        // Keepalive à part : annuler un ReceiveAsync de ClientWebSocket ABANDONNE la connexion, on ne le coupe donc jamais.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = new SemaphoreSlim(1, 1);
        _sendLock = send;
        var keepalive = Task.Run(async () =>
        {
            try
            {
                while (!pumpCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await Task.Delay(Keepalive, pumpCts.Token);
                    await SendAsync(ws, "{\"type\":\"ping\"}", pumpCts.Token);
                }
            }
            catch (Exception) { }
        });
        var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Jam : dealer fermé ({Status})", result.CloseStatus);
                        return;
                    }
                    ms.Write(buffer.Array!, 0, result.Count);
                } while (!result.EndOfMessage);
                await HandleFrameAsync(ws, accessToken, ms.ToArray(), ct);
            }
            _log.LogInformation("Jam : dealer terminé (état {State})", ws.State);
        }
        finally
        {
            pumpCts.Cancel();
            await keepalive;
        }
    }

    private SemaphoreSlim? _sendLock;

    private async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var gate = _sendLock;
        if (gate is not null) await gate.WaitAsync(ct);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
        finally { gate?.Release(); }
    }

    /// <summary>Traite une trame dealer selon son type (voir go-librespot) : message (push), request (commande à acquitter), pong.</summary>
    private async Task HandleFrameAsync(ClientWebSocket ws, string accessToken, byte[] frame, CancellationToken ct)
    {
        string? type = null, uri = null, key = null;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            type = Str(root, "type");
            uri = Str(root, "uri");
            key = Str(root, "key");
            if (type == "message" && uri is not null && DecodeMessagePayload(root) is { } payload)
            {
                if (uri.StartsWith("hm://connect-state/v1/cluster", StringComparison.Ordinal))
                {
                    if (ClusterState.ParseClusterUpdate(payload) is { } cluster) await ProcessAsync(accessToken, cluster, ct);
                }
                else if (uri.StartsWith(SessionUpdate.Uri, StringComparison.Ordinal))
                {
                    var reason = SessionUpdate.Reason(payload);
                    _log.LogInformation("Jam : session mise à jour ({Reason})", reason ?? "?");
                    if (SessionUpdate.EndsJam(payload))
                    {
                        // Fini pour nous : on ferme, la boucle de suivi verra _over et ne se reconnectera pas.
                        _over = true;
                        ws.Abort();
                        return;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        // Les « request » (commandes de lecture visant cet appareil) doivent être acquittées, sinon le dealer peut cesser d'émettre.
        if (type == "request" && key is not null)
        {
            var reply = $"{{\"type\":\"reply\",\"key\":\"{key}\",\"payload\":{{\"success\":true}}}}";
            try { await SendAsync(ws, reply, ct); }
            catch (Exception) { }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Décode le payload d'une trame « message » : payloads[0] base64, décompressé si Transfer-Encoding gzip (ou magie gzip).</summary>
    private static byte[]? DecodeMessagePayload(JsonElement root)
    {
        if (!root.TryGetProperty("payloads", out var pl) || pl.ValueKind != JsonValueKind.Array || pl.GetArrayLength() == 0) return null;
        var first = pl[0];
        byte[] bytes;
        try { bytes = first.ValueKind == JsonValueKind.String ? Convert.FromBase64String(first.GetString()!) : Encoding.UTF8.GetBytes(first.GetRawText()); }
        catch (FormatException) { return null; }
        return Gunzip(bytes);
    }


    private static byte[] Gunzip(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return data;
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    private async Task<string?> ReadConnectionIdAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[32 * 1024]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 5; i++)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, timeout.Token);
                ms.Write(buffer.Array!, 0, result.Count);
            } while (!result.EndOfMessage);
            try
            {
                using var doc = JsonDocument.Parse(ms.ToArray());
                if (doc.RootElement.TryGetProperty("headers", out var h) && h.TryGetProperty("Spotify-Connection-Id", out var id))
                    return id.GetString();
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>Traite un nouvel état de l'hôte : si le titre a changé, (re)résout ; sinon met à jour position/pause.</summary>
    private async Task ProcessAsync(string accessToken, ClusterState cluster, CancellationToken ct)
    {
        _lastCluster = cluster;
        if (cluster.ServerTimestampMs > 0)
        {
            var sample = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cluster.ServerTimestampMs;
            if (_spotifyClockOffsetMs is not { } best || sample < best) _spotifyClockOffsetMs = sample;
        }
        var trackId = cluster.TrackId;
        // Pub, épisode, fichier local : pas un titre à suivre, le précédent n'est plus le titre en cours.
        _lastTrackId = trackId;
        // File de l'hôte : on prépare d'avance les prochains titres pour que le passage soit immédiat.
        _nextTrackIds = (cluster.NextTrackUris ?? [])
            .Select(u => u.StartsWith("spotify:track:", StringComparison.Ordinal) ? u["spotify:track:".Length..] : null)
            .OfType<string>().Where(id => id != trackId).Distinct().Take(PrefetchAhead).ToList();
        if (trackId is not null) Prepare(accessToken, trackId, cluster.DurationMs / 1000.0, ct);
        foreach (var next in _nextTrackIds) Prepare(accessToken, next, 0, ct);
        Publish();
    }

    /// <summary>Résout le titre courant : bibliothèque d'abord, puis import YouTube en mode invité.</summary>
    /// <summary>Titres suivants préparés d'avance (métadonnées, bibliothèque, import YouTube) pour un démarrage immédiat.</summary>
    private const int PrefetchAhead = 2;
    private const int PrepCacheSize = 40;
    private static readonly TimeSpan ImportGiveUp = TimeSpan.FromMinutes(4);

    /// <summary>Préparation d'un titre Spotify : où en est sa résolution en un titre jouable ici.</summary>
    private sealed class Prep
    {
        public JamTrackDto? Meta;
        public TrackDto? Resolved;
        public JamResolve State = JamResolve.Pending;
        public long Order;
        /// <summary>Échec passager (délai, réseau, import abandonné) : à retenter après ce moment.</summary>
        public DateTimeOffset? RetryAfter;
    }

    private readonly ConcurrentDictionary<string, Prep> _preps = new();
    private long _prepOrder;

    /// <summary>Lance (une fois) la préparation d'un titre ; rend son suivi.</summary>
    private Prep Prepare(string accessToken, string trackId, double durationSeconds, CancellationToken ct)
    {
        if (_preps.TryGetValue(trackId, out var existing))
        {
            if (existing.RetryAfter is not { } retry || retry > DateTimeOffset.UtcNow) return existing;
            // Échoué pour une raison passagère : on retente au lieu de garder l'échec pour tout le Jam.
            _preps.TryRemove(new KeyValuePair<string, Prep>(trackId, existing));
        }
        var prep = new Prep { Order = Interlocked.Increment(ref _prepOrder) };
        if (!_preps.TryAdd(trackId, prep)) return _preps[trackId];
        // Le cache reste petit : on oublie les plus anciennes préparations (sauf celle en cours).
        if (_preps.Count > PrepCacheSize)
            foreach (var old in _preps.Where(p => p.Key != _lastTrackId).OrderBy(p => p.Value.Order).Take(_preps.Count - PrepCacheSize).ToList())
                _preps.TryRemove(old.Key, out _);
        _ = Task.Run(() => RunPrepAsync(accessToken, trackId, durationSeconds, prep, ct), ct);
        return prep;
    }

    private async Task RunPrepAsync(string accessToken, string trackId, double durationSeconds, Prep prep, CancellationToken ct)
    {
        try
        {
            using (var scope = _scopes.CreateScope())
            {
                var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
                var meta = await client.TrackAsync(accessToken, trackId, ct);
                // Titre et artistes : page d'intégration ; durée : celle du cluster si connue (plus fiable).
                prep.Meta = meta is null ? null : durationSeconds > 0 ? meta with { DurationSeconds = durationSeconds } : meta;
            }
            if (prep.Meta is null) { Fail(trackId, prep, RetrySoon); return; }
            Changed(trackId);

            string? videoId;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var resolver = scope.ServiceProvider.GetRequiredService<JamResolver>();
                if (await resolver.FindInLibraryAsync(db, _userId, prep.Meta, ct) is { } inLibrary)
                {
                    SetPrep(trackId, prep, JamResolve.Matched, inLibrary);
                    return;
                }
                // Son chez l'invité : import YouTube pour pouvoir jouer. En « distant seul », inutile.
                if (_audio != JamAudio.Guest) { SetPrep(trackId, prep, JamResolve.Unavailable, null); return; }
                videoId = await resolver.StartImportAsync(db, prep.Meta, ct);
            }
            if (videoId is null) { Fail(trackId, prep, RetryLater); return; }
            SetPrep(trackId, prep, JamResolve.Importing, null);

            var deadline = DateTimeOffset.UtcNow + ImportGiveUp;
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(ImportPoll, ct);
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var resolver = scope.ServiceProvider.GetRequiredService<JamResolver>();
                var outcome = await resolver.CheckImportAsync(db, _userId, videoId, ct);
                if (outcome.State == JamResolve.Matched) { SetPrep(trackId, prep, JamResolve.Matched, outcome.Track); return; }
                if (outcome.State == JamResolve.Unavailable) { Fail(trackId, prep, RetryLater); return; }
            }
            Fail(trackId, prep, RetryLater); // Import trop long : abandonné pour l'instant.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Jam : préparation du titre {Track} impossible", trackId);
            Fail(trackId, prep, RetrySoon);
        }
    }

    /// <summary>Délais avant de retenter un titre : métadonnées ou erreur passagère, puis recherche ou import ratés.</summary>
    private static readonly TimeSpan RetrySoon = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryLater = TimeSpan.FromMinutes(10);

    /// <summary>Introuvable pour l'instant : publié comme tel, et retenté si l'hôte rejoue le titre plus tard.</summary>
    private void Fail(string trackId, Prep prep, TimeSpan retryIn)
    {
        prep.RetryAfter = DateTimeOffset.UtcNow + retryIn;
        SetPrep(trackId, prep, JamResolve.Unavailable, null);
    }

    private void SetPrep(string trackId, Prep prep, JamResolve state, TrackDto? resolved)
    {
        _log.LogDebug("Jam : titre {Track} → {State}", trackId, state);
        prep.State = state;
        prep.Resolved = resolved;
        Changed(trackId);
    }

    /// <summary>Une préparation a avancé : republie si elle concerne le titre en cours ou un des suivants.</summary>
    private void Changed(string trackId)
    {
        if (trackId == _lastTrackId || _nextTrackIds.Contains(trackId)) Publish();
    }

    private IReadOnlyList<string> _nextTrackIds = [];

    /// <summary>Jeton d'accès frais : rafraîchi et réenregistré s'il expire bientôt.</summary>
    private async Task<string?> FreshAccessTokenAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SonNuageDbContext>();
        var tokens = sp.GetRequiredService<SpotifyTokens>();
        var client = sp.GetRequiredService<SpotifyClient>();
        var row = await db.Users.Where(u => u.Id == _userId)
            .Select(u => new { u.SpotifyAccessToken, u.SpotifyRefreshToken, u.SpotifyExpiresAt, u.SpotifyDisplayName })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var access = tokens.Read(row.SpotifyAccessToken);
        if (access is not null && row.SpotifyExpiresAt is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(1)) return access;

        var refresh = tokens.Read(row.SpotifyRefreshToken);
        if (refresh is null) return access; // Pas de rafraîchissement : on tente le jeton existant.
        var fresh = await client.RefreshAsync(refresh, ct);
        if (fresh is null) return access;
        await tokens.SaveAsync(db, _userId, fresh.AccessToken, fresh.RefreshToken ?? refresh, fresh.ExpiresAt, row.SpotifyDisplayName, ct);
        return fresh.AccessToken;
    }

    private void Publish() => _publish(_userId, Build());

    private JamStateDto Build()
    {
        var now = DateTimeOffset.UtcNow;
        var current = _lastTrackId is { } id && _preps.TryGetValue(id, out var p) ? p : null;
        var upcoming = _nextTrackIds
            .Select(n => _preps.TryGetValue(n, out var np) && np.State == JamResolve.Matched ? np.Resolved : null)
            .TakeWhile(t => t is not null).OfType<TrackDto>().ToList();
        return new JamStateDto(
            Active: true,
            Service: Contracts.Jam.Spotify,
            Audio: _audio,
            HostName: _hostName,
            Track: current?.Meta ?? (_lastCluster is { TrackId: not null } c ? new JamTrackDto(c.Title ?? "", "", null, c.DurationMs / 1000.0, null) : null),
            IsPlaying: _lastCluster is { IsPaused: false },
            PositionSeconds: _lastCluster?.PositionSecondsAt(now, _spotifyClockOffsetMs ?? 0) ?? 0,
            AsOfUnixMs: now.ToUnixTimeMilliseconds(),
            Resolve: current?.State ?? JamResolve.Pending,
            Resolved: current?.Resolved,
            Upcoming: upcoming,
            SourceTrackId: _lastTrackId);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // Déjà journalisé.
        }
        _cts.Dispose();
        if (!_over) await LeaveSessionAsync();
    }

    /// <summary>Quitte le Jam côté Spotify (au mieux, sans bloquer l'arrêt) : on cesse d'y apparaître comme membre.</summary>
    private async Task LeaveSessionAsync()
    {
        if (_session is not { } session) return;
        try
        {
            using var timeout = new CancellationTokenSource(LeaveTimeout);
            var accessToken = await FreshAccessTokenAsync(timeout.Token);
            if (accessToken is null) return;
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SpotifyClient>().LeaveAsync(session.Hosts, accessToken, session.Id, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            _log.LogInformation("Jam : impossible de quitter la session côté Spotify ({Error})", ex.Message);
        }
    }

    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(5);
}
