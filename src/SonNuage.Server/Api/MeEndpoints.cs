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

        // Mot de passe des applis Subsonic : relisible par son propriétaire (il le saisit dans chaque appli).
        g.MapGet("/subsonic", async (HttpContext http, SonNuageDbContext db, Subsonic.SubsonicPasswords passwords, CancellationToken ct) =>
            await db.Users.FindAsync([http.User.UserId()], ct) is { } user ? Results.Ok(Access(user, passwords.Read(user))) : Results.Unauthorized());

        g.MapPost("/subsonic", async (HttpContext http, SonNuageDbContext db, Subsonic.SubsonicPasswords passwords, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.Users.FindAsync([http.User.UserId()], ct) is not { } user) return Results.Unauthorized();
            var password = await passwords.CreateAsync(db, user, time.GetUtcNow(), ct);
            return Results.Ok(Access(user, password));
        });

        g.MapDelete("/subsonic", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (await db.Users.FindAsync([http.User.UserId()], ct) is not { } user) return Results.Unauthorized();
            await Subsonic.SubsonicPasswords.RevokeAsync(db, user, ct);
            return Results.NoContent();
        });

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

        g.MapGet("/favorites/albums", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.FavoriteAlbums.Where(f => f.UserId == userId).OrderByDescending(f => f.CreatedAt)
                .Select(f => f.Album).Select(Projections.Album).ToListAsync(ct);
        });

        g.MapPut("/favorites/albums/{albumId:guid}", async (Guid albumId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (!await db.Albums.AnyAsync(a => a.Id == albumId, ct)) return Results.NotFound();
            if (!await db.FavoriteAlbums.AnyAsync(f => f.UserId == userId && f.AlbumId == albumId, ct))
            {
                db.FavoriteAlbums.Add(new FavoriteAlbum { UserId = userId, AlbumId = albumId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        g.MapDelete("/favorites/albums/{albumId:guid}", async (Guid albumId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.FavoriteAlbums.Where(f => f.UserId == userId && f.AlbumId == albumId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/favorites/artists", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.FavoriteArtists.Where(f => f.UserId == userId).OrderByDescending(f => f.CreatedAt)
                .Select(f => f.Artist).Select(Projections.Artist).ToListAsync(ct);
        });

        g.MapPut("/favorites/artists/{artistId:guid}", async (Guid artistId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (!await db.Artists.AnyAsync(a => a.Id == artistId, ct)) return Results.NotFound();
            if (!await db.FavoriteArtists.AnyAsync(f => f.UserId == userId && f.ArtistId == artistId, ct))
            {
                db.FavoriteArtists.Add(new FavoriteArtist { UserId = userId, ArtistId = artistId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        g.MapDelete("/favorites/artists/{artistId:guid}", async (Guid artistId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.FavoriteArtists.Where(f => f.UserId == userId && f.ArtistId == artistId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // Note d'un titre, d'un album ou d'un artiste (les mêmes que dans les applis Subsonic).
        g.MapPut("/ratings/{itemId:guid}", async (Guid itemId, RatingRequest req, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (req.Value is < 0 or > UserRating.Max) return Results.BadRequest();
            var exists = await db.Tracks.AnyAsync(t => t.Id == itemId, ct) || await db.Albums.AnyAsync(a => a.Id == itemId, ct)
                || await db.Artists.AnyAsync(a => a.Id == itemId, ct);
            if (!exists) return Results.NotFound();
            await UserRatings.SetAsync(db, http.User.UserId(), itemId, req.Value, time.GetUtcNow(), ct);
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

    private static SubsonicAccessDto Access(User user, string? password) =>
        new(password is not null, user.Username, password, password is null ? null : user.SubsonicPasswordCreatedAt);
}
