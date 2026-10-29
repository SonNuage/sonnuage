using Localify.Contracts.Localization;
using Localify.Contracts;
using Localify.Server.Auth;
using Localify.Server.Data;
using Localify.Server.Youtube;
using Microsoft.EntityFrameworkCore;

namespace Localify.Server.Api;

/// <summary>Extension de navigateur : clés gérées par l'admin, et état d'une vidéo vu depuis YouTube.</summary>
public static class ExtensionEndpoints
{
    public static void MapExtensionEndpoints(this IEndpointRouteBuilder app)
    {
        var keys = app.MapGroup("/api/admin/extension-keys").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Extension");

        keys.MapGet("/", async (LocalifyDbContext db, CancellationToken ct) =>
            await db.ApiKeys.OrderByDescending(k => k.CreatedAt)
                .Select(k => new ExtensionKeyDto(k.Id, k.Name, k.Prefix, k.CreatedBy.Username, k.CreatedAt, k.LastUsedAt))
                .ToListAsync(ct));

        keys.MapPost("/", async (CreateExtensionKeyRequest req, HttpContext http, LocalifyDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100) return Results.Problem(Tx.Key("errors.keys.nameRequired"), statusCode: 400);
            if (!Guid.TryParse(http.User.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
            var secret = ApiKeyAuthenticationHandler.NewSecret();
            var key = new ApiKey
            {
                Name = name,
                Kind = ApiKeyKind.BrowserExtension,
                KeyHash = ApiKeyAuthenticationHandler.Hash(secret),
                Prefix = secret[..12],
                CreatedById = userId,
                CreatedAt = time.GetUtcNow(),
            };
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync(ct);
            var username = http.User.Identity?.Name ?? "";
            return Results.Ok(new CreatedExtensionKeyDto(new ExtensionKeyDto(key.Id, key.Name, key.Prefix, username, key.CreatedAt, null), secret));
        });

        keys.MapDelete("/{id:guid}", async (Guid id, LocalifyDbContext db, CancellationToken ct) =>
            await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent());

        app.MapGet("/api/extension/status", async (string? videoId, string? url, LocalifyDbContext db, CancellationToken ct) =>
        {
            if (videoId is not null && !YoutubeImporter.IsVideoId(videoId)) return Results.Problem(Tx.Key("errors.badVideoId"), statusCode: 400);
            var item = videoId is null ? null : await db.YoutubeItems.Where(i => i.VideoId == videoId)
                .Select(i => new { i.Status, i.NeedsReview, i.Error }).FirstOrDefaultAsync(ct);
            bool? subscribed = null;
            if (url is not null)
            {
                if (YoutubeSyncService.NormalizeUrl(url) is not { } normalized) return Results.Problem("Adresse YouTube invalide.", statusCode: 400);
                subscribed = await db.YoutubeSubscriptions.AnyAsync(s => s.Url == normalized, ct);
            }
            return Results.Ok(new ExtensionStatusDto(videoId, item?.Status.ToString(), item?.NeedsReview ?? false, item?.Error, subscribed));
        }).RequireAuthorization(AuthConstants.ExtensionPolicy).WithTags("Extension");
    }
}
