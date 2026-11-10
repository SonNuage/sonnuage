using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.ListenBrainz;

namespace SonNuage.Server.Api;

/// <summary>
/// ListenBrainz, niveau 2 (facultatif, par compte) : jeton validé auprès de ListenBrainz puis chiffré, envoi des
/// écoutes (désactivé par défaut) et recommandations personnelles. Rien ne part vers ListenBrainz sans jeton enregistré.
/// </summary>
public static class ListenBrainzEndpoints
{
    public static void MapListenBrainzEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me/listenbrainz").RequireAuthorization().WithTags("Me");

        g.MapGet("", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await ListenBrainzTokens.AccountAsync(db, http.User.UserId(), ct));

        g.MapPut("/token", async (ListenBrainzTokenRequest req, HttpContext http, SonNuageDbContext db, ListenBrainzApi api, ListenBrainzTokens tokens,
            CancellationToken ct) =>
        {
            var token = req.Token?.Trim() ?? "";
            if (token.Length is 0 or > ListenBrainzTokenRequest.MaxLength) return Results.Problem(Tx.Key("errors.listenbrainz.invalidToken"), statusCode: 400);
            string? userName;
            try
            {
                userName = await api.ValidateTokenAsync(token, ct);
            }
            catch (ListenBrainzUnavailableException)
            {
                return Results.Problem(Tx.Key("errors.listenbrainz.unreachable"), statusCode: 502);
            }
            if (userName is null) return Results.Problem(Tx.Key("errors.listenbrainz.invalidToken"), statusCode: 400);
            var userId = http.User.UserId();
            var protectedToken = tokens.Protect(token);
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.ListenBrainzToken, protectedToken)
                .SetProperty(u => u.ListenBrainzUserName, userName), ct);
            return Results.Ok(await ListenBrainzTokens.AccountAsync(db, userId, ct));
        });

        g.MapDelete("/token", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            await ListenBrainzTokens.DisconnectAsync(db, http.User.UserId(), ct);
            return Results.NoContent();
        });

        g.MapPut("/submit", async (ListenBrainzSubmitRequest req, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var user = await db.Users.Where(u => u.Id == userId).Select(u => new { u.ListenBrainzToken, u.ListenBrainzSubmitSince }).SingleAsync(ct);
            if (req.Enabled && user.ListenBrainzToken is null) return Results.Problem(Tx.Key("errors.listenbrainz.noToken"), statusCode: 409);
            // Déjà activé : la date d'origine reste (les écoutes faites depuis partent toujours).
            DateTimeOffset? since = req.Enabled ? user.ListenBrainzSubmitSince ?? time.GetUtcNow() : null;
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ListenBrainzSubmitSince, since), ct);
            return Results.Ok(await ListenBrainzTokens.AccountAsync(db, userId, ct));
        });

        g.MapGet("/recommendations", async (HttpContext http, SonNuageDbContext db, ListenBrainzRecommendations recommendations, CancellationToken ct) =>
        {
            var canRequest = http.User.IsAdmin() || http.User.HasClaim("role", Roles.Contributor);
            return await recommendations.ForAsync(db, http.User.UserId(), canRequest, ct);
        });
    }
}
