using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Api;

public static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me").RequireAuthorization().WithTags("Me");

        g.MapGet("/favorites", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.Favorites.Where(f => f.UserId == userId).OrderByDescending(f => f.CreatedAt)
                .Select(f => f.Track).Select(Projections.Track(db, userId)).ToListAsync(ct);
        });

        g.MapPut("/favorites/{trackId:guid}", async (Guid trackId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (!await db.Tracks.AnyAsync(t => t.Id == trackId, ct)) return Results.NotFound();
            if (!await db.Favorites.AnyAsync(f => f.UserId == userId && f.TrackId == trackId, ct))
            {
                db.Favorites.Add(new Favorite { UserId = userId, TrackId = trackId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        g.MapDelete("/favorites/{trackId:guid}", async (Guid trackId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.Favorites.Where(f => f.UserId == userId && f.TrackId == trackId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/plays", async (PlayEventRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tracks.AnyAsync(t => t.Id == req.TrackId, ct)) return Results.NotFound();
            db.PlayEvents.Add(new PlayEvent
            {
                UserId = http.User.UserId(),
                TrackId = req.TrackId,
                SecondsPlayed = Math.Max(0, req.SecondsPlayed),
                Completed = req.Completed,
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/playback", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.Users.Where(u => u.Id == userId)
                .Select(u => new PlaybackPreferences(u.CrossfadeSeconds, u.InfinitePlay, u.InfiniteAhead)).SingleAsync(ct);
        });

        g.MapPut("/playback", async (PlaybackPreferences req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var prefs = req.Clamped();
            var userId = http.User.UserId();
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.CrossfadeSeconds, prefs.CrossfadeSeconds)
                .SetProperty(u => u.InfinitePlay, prefs.InfinitePlay)
                .SetProperty(u => u.InfiniteAhead, prefs.InfiniteAhead), ct);
            return prefs;
        });

        g.MapGet("/recent", async (int? count, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var ids = await LibraryEndpoints.RecentTrackIdsAsync(db, userId, Math.Clamp(count ?? 50, 1, 200), ct);
            return await LibraryEndpoints.TracksInOrderAsync(db, userId, ids, ct);
        });
    }
}
