using Localify.Contracts;
using Localify.Server.Auth;
using Localify.Server.Data;
using Localify.Server.Library;
using Localify.Server.Search;
using Localify.Server.Streaming;
using Microsoft.EntityFrameworkCore;

namespace Localify.Server.Api;

public static class LibraryEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    public static void MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info", (TranscodeService transcoder) =>
        {
            var qualities = Enum.GetValues<StreamQuality>()
                .Where(q => q == StreamQuality.Original || transcoder.Available)
                .Select(q => TranscodeProfile.All.FirstOrDefault(p => p.Quality == q) is { } p
                    ? new QualityDto(q.ToWire(), q.Label(), p.Extension, p.BitrateKbps)
                    : new QualityDto(q.ToWire(), q.Label(), "original", null))
                .ToList();
            var version = typeof(LibraryEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            return new ServerInfoDto("Localify", version, qualities, transcoder.Available);
        }).AllowAnonymous();

        var g = app.MapGroup("/api").RequireAuthorization().WithTags("Library");

        g.MapGet("/home", async (HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var recentAlbums = await db.Albums.OrderByDescending(a => a.CreatedAt).Take(20).Select(Projections.Album).ToListAsync(ct);
            var recentTrackIds = await RecentTrackIdsAsync(db, userId, 20, ct);
            var recentTracks = await TracksInOrderAsync(db, userId, recentTrackIds, ct);
            var playlists = await db.Playlists.Where(p => p.OwnerId == userId).OrderByDescending(p => p.UpdatedAt).Take(20)
                .Select(Projections.Playlist).ToListAsync(ct);
            return new HomeDto(recentAlbums, recentTracks, playlists);
        });

        g.MapGet("/artists", async (string? q, int? offset, int? limit, LocalifyDbContext db, CancellationToken ct) =>
        {
            // Seulement les artistes qui ont un album : un crédit de titre (« X feat. Y ») n'est pas un artiste de la bibliothèque.
            var query = db.Artists.Where(a => a.Albums.Any());
            if (!string.IsNullOrWhiteSpace(q))
            {
                var key = NameNormalizer.Key(q);
                query = query.Where(a => a.NormalizedName.Contains(key));
            }
            return await PageAsync(query.OrderBy(a => a.SortName).Select(Projections.Artist), offset, limit, ct);
        });

        g.MapGet("/artists/{id:guid}", async (Guid id, LocalifyDbContext db, CancellationToken ct) =>
        {
            var artist = await db.Artists.Where(a => a.Id == id).Select(Projections.Artist).FirstOrDefaultAsync(ct);
            if (artist is null) return Results.NotFound();
            var albums = await db.Albums.Where(a => a.ArtistId == id).OrderBy(a => a.Year).ThenBy(a => a.Title)
                .Select(Projections.Album).ToListAsync(ct);
            return Results.Ok(new ArtistDetailDto(artist, albums));
        });

        g.MapGet("/artists/{id:guid}/tracks", async (Guid id, HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
            await db.Tracks.Where(t => t.Album.ArtistId == id || t.ArtistId == id)
                .OrderBy(t => t.Album.Year).ThenBy(t => t.Album.Title).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct));

        g.MapGet("/albums", async (string? sort, string? q, int? offset, int? limit, LocalifyDbContext db, CancellationToken ct) =>
        {
            var query = db.Albums.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var key = NameNormalizer.Key(q);
                query = query.Where(a => a.NormalizedTitle.Contains(key) || a.Artist.NormalizedName.Contains(key));
            }
            query = sort switch
            {
                "recent" => query.OrderByDescending(a => a.CreatedAt),
                "year" => query.OrderByDescending(a => a.Year).ThenBy(a => a.Title),
                "artist" => query.OrderBy(a => a.Artist.SortName).ThenBy(a => a.Year),
                _ => query.OrderBy(a => a.Title),
            };
            return await PageAsync(query.Select(Projections.Album), offset, limit, ct);
        });

        g.MapGet("/albums/{id:guid}", async (Guid id, HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
        {
            var album = await db.Albums.Where(a => a.Id == id).Select(Projections.Album).FirstOrDefaultAsync(ct);
            if (album is null) return Results.NotFound();
            var tracks = await db.Tracks.Where(t => t.AlbumId == id)
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title)
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct);
            return Results.Ok(new AlbumDetailDto(album, tracks));
        });

        g.MapGet("/tracks/{id:guid}", async (Guid id, HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
            await db.Tracks.Where(t => t.Id == id).Select(Projections.Track(db, http.User.UserId())).FirstOrDefaultAsync(ct)
                is { } t ? Results.Ok(t) : Results.NotFound());

        // Résolution groupée : restauration d'une file d'attente, Android Auto.
        g.MapPost("/tracks/lookup", async (List<Guid> ids, HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
            await TracksInOrderAsync(db, http.User.UserId(), ids.Take(1000).ToList(), ct));

        // Mélange de toute la bibliothèque (« Radio », lecture aléatoire globale).
        g.MapGet("/tracks/random", async (int? count, HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
            await db.Tracks.OrderBy(_ => EF.Functions.Random()).Take(Math.Clamp(count ?? 50, 1, 500))
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct));

        g.MapGet("/search", (string q, int? limit, HttpContext http, SearchService search, CancellationToken ct) =>
            search.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), http.User.UserId(), ct));

        g.MapGet("/search/suggest", (string q, int? limit, SearchService search, CancellationToken ct) =>
            search.SuggestAsync(q, Math.Clamp(limit ?? 8, 1, 20), ct));

        app.MapMethods("/api/albums/{id:guid}/cover", ["GET", "HEAD"], async (Guid id, int? size, HttpContext http, LocalifyDbContext db, CoverService covers, CancellationToken ct) =>
        {
            var album = await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (album is null) return Results.NotFound();
            var cover = await covers.GetAsync(db, album, size, ct);
            if (cover is null) return Results.NotFound();

            http.Response.Headers.CacheControl = "private, max-age=604800";
            var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{album.Id:N}-{album.CoverVersion}-{size}\"");
            return Results.File(cover.Value.Path, cover.Value.ContentType, entityTag: etag, enableRangeProcessing: false);
        }).RequireAuthorization(AuthConstants.MediaPolicy).WithTags("Media");
    }

    internal static async Task<List<Guid>> RecentTrackIdsAsync(LocalifyDbContext db, Guid userId, int count, CancellationToken ct)
    {
        var recent = await db.PlayEvents.Where(p => p.UserId == userId)
            .OrderByDescending(p => p.PlayedAt).Take(count * 5)
            .Select(p => p.TrackId).ToListAsync(ct);
        return recent.Distinct().Take(count).ToList();
    }

    internal static async Task<List<TrackDto>> TracksInOrderAsync(LocalifyDbContext db, Guid userId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var found = await db.Tracks.Where(t => ids.Contains(t.Id)).Select(Projections.Track(db, userId)).ToDictionaryAsync(t => t.Id, ct);
        return ids.Where(found.ContainsKey).Select(id => found[id]).ToList();
    }

    private static async Task<PagedResult<T>> PageAsync<T>(IQueryable<T> query, int? offset, int? limit, CancellationToken ct)
    {
        var o = Math.Max(0, offset ?? 0);
        var l = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var total = await query.CountAsync(ct);
        var items = await query.Skip(o).Take(l).ToListAsync(ct);
        return new PagedResult<T>(items, total, o, l);
    }
}
