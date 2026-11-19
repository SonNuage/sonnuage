using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jam;

namespace SonNuage.Server.Api;

/// <summary>
/// « Rejoindre un Jam » (par compte, facultatif) : le compte relie son service d'écoute (Spotify), colle ou scanne un
/// lien de Jam, et SonNuage suit en direct ce que joue l'hôte pour le rejouer ici (bibliothèque ou YouTube). Les jetons
/// du service, chiffrés, ne servent qu'au serveur. Le son du service n'est jamais récupéré.
/// </summary>
public static class JamEndpoints
{
    public static void MapJamEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me/jam").RequireAuthorization().WithTags("Me");

        // Liaison du compte Spotify (jetons obtenus par l'appli, confiés au serveur qui les chiffre).
        g.MapGet("/spotify", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await SpotifyTokens.AccountAsync(db, http.User.UserId(), ct));

        g.MapPut("/spotify/token", async (MusicServiceTokenRequest req, HttpContext http, SonNuageDbContext db, SpotifyTokens tokens,
            SpotifyClient client, CancellationToken ct) =>
        {
            if (req.Service != Contracts.Jam.Spotify) return Results.Problem(Tx.Key("errors.jam.service"), statusCode: 400);
            var access = req.AccessToken?.Trim() ?? "";
            if (access.Length is 0 or > Contracts.Jam.MaxTokenLength) return Results.Problem(Tx.Key("errors.jam.token"), statusCode: 400);
            var userId = http.User.UserId();
            // Nom affiché : celui donné par l'appli, sinon demandé au service. Pas de validation du jeton ici : l'API Web
            // de Spotify limite fortement ce type de jeton (429) ; un jeton refusé se verra au premier « join ».
            var displayName = req.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName)) displayName = await client.DisplayNameAsync(access, ct);
            var expires = DateTimeOffset.UtcNow.AddSeconds(req.ExpiresInSeconds is > 0 and < 86_400 ? req.ExpiresInSeconds : 3600);
            await tokens.SaveAsync(db, userId, access, req.RefreshToken?.Trim(), expires, displayName, ct);
            return Results.Ok(await SpotifyTokens.AccountAsync(db, userId, ct));
        });

        g.MapDelete("/spotify/token", async (HttpContext http, SonNuageDbContext db, JamHub hub, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await hub.LeaveAsync(userId);
            await SpotifyTokens.DisconnectAsync(db, userId, ct);
            return Results.NoContent();
        });

        // Rejoindre un Jam depuis un lien (collé ou scanné) ; le mode décide si le son joue aussi ici.
        g.MapPost("/join", async (JamJoinRequest req, HttpContext http, SonNuageDbContext db, SpotifyTokens tokens, SpotifyClient client,
            JamHub hub, CancellationToken ct) =>
        {
            var link = req.Link?.Trim() ?? "";
            if (link.Length is 0 or > Contracts.Jam.MaxLinkLength) return Results.Problem(Tx.Key("errors.jam.link"), statusCode: 400);
            var service = JamJoinRequest.Service(link) ?? Contracts.Jam.Spotify;
            if (service != Contracts.Jam.Spotify) return Results.Problem(Tx.Key("errors.jam.service"), statusCode: 400);
            var userId = http.User.UserId();
            var connected = await db.Users.AnyAsync(u => u.Id == userId && u.SpotifyAccessToken != null, ct);
            if (!connected) return Results.Problem(Tx.Key("errors.jam.notConnected"), statusCode: 409);
            var token = await SpotifyLink.ResolveAsync(client.Http, link, ct);
            if (token is null) return Results.Problem(Tx.Key("errors.jam.badLink"), statusCode: 400);
            if (!await hub.JoinAsync(userId, token, req.Audio, ct)) return Results.Problem(Tx.Key("errors.jam.refused"), statusCode: 409);
            return Results.Text(JamHub.Serialize(hub.Current(userId)), "application/json");
        });

        g.MapPost("/leave", async (HttpContext http, JamHub hub) =>
        {
            await hub.LeaveAsync(http.User.UserId());
            return Results.NoContent();
        });

        g.MapGet("/state", (HttpContext http, JamHub hub) => Results.Text(JamHub.Serialize(hub.Current(http.User.UserId())), "application/json"));

        // Flux d'état du Jam (Server-Sent Events) : un « state » à l'abonnement, puis à chaque changement.
        g.MapGet("/stream", (HttpContext http, JamHub hub) => new JamStream(http.User.UserId(), hub));
    }

    /// <summary>Flux SSE de l'état d'un Jam pour un appareil : « state » initial, puis à chaque mise à jour, avec battement de cœur.</summary>
    private sealed class JamStream(Guid userId, JamHub hub) : IResult
    {
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10);

        public async Task ExecuteAsync(HttpContext http)
        {
            var ct = http.RequestAborted;
            var response = http.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var (reader, lease) = hub.Subscribe(userId);
            try
            {
                while (true)
                {
                    using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    beat.CancelAfter(Heartbeat);
                    bool more;
                    try
                    {
                        more = await reader.WaitToReadAsync(beat.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Battement : un état frais plutôt qu'un simple ping, pour que l'appareil affine sa référence d'horloge.
                        await response.WriteAsync($"event: {Contracts.Jam.StateEvent}\ndata: {JamHub.Serialize(hub.Current(userId))}\n\n", ct);
                        await response.Body.FlushAsync(ct);
                        continue;
                    }
                    if (!more) break;
                    while (reader.TryRead(out var json))
                        await response.WriteAsync($"event: {Contracts.Jam.StateEvent}\ndata: {json}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Appareil parti.
            }
            finally
            {
                lease.Dispose();
            }
        }
    }
}
