using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Search;
using SonNuage.Server.Streaming;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Api;

public static class LibraryEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    public static void MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info", (TranscodeService transcoder, IWebHostEnvironment env) =>
        {
            var qualities = Enum.GetValues<StreamQuality>()
                .Where(q => q == StreamQuality.Original || transcoder.Available)
                .Select(q => TranscodeProfile.All.FirstOrDefault(p => p.Quality == q) is { } p
                    ? new QualityDto(q.ToWire(), q.Label(), p.Extension, p.BitrateKbps)
                    : new QualityDto(q.ToWire(), q.Label(), "original", null))
                .ToList();
            var version = AppVersion.Of(typeof(LibraryEndpoints).Assembly);
            var runtime = $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
            return new ServerInfoDto("SonNuage", version, qualities, transcoder.Available, WebApp.Build(env), runtime);
        }).AllowAnonymous();

        var g = app.MapGroup("/api").RequireAuthorization().WithTags("Library");

        g.MapGet("/home", async (HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var recentAlbums = await db.Albums.OrderByDescending(a => a.CreatedAt).Take(20).Select(Projections.Album).ToListAsync(ct);
            var recentTrackIds = await RecentTrackIdsAsync(db, userId, 20, ct);
            var recentTracks = await TracksInOrderAsync(db, userId, recentTrackIds, ct);
            var playlists = await db.Playlists.Where(p => p.OwnerId == userId).OrderByDescending(p => p.UpdatedAt).Take(20)
                .Select(Projections.Playlist).ToListAsync(ct);
            var rediscover = await TracksInOrderAsync(db, userId,
                await HomeDiscovery.RediscoverTrackIdsAsync(db, userId, time.GetUtcNow(), 20, ct), ct);
            var notYetPlayedIds = await HomeDiscovery.NotYetPlayedAlbumIdsAsync(db, userId, 20, ct);
            var notYetPlayed = await db.Albums.Where(a => notYetPlayedIds.Contains(a.Id)).Select(Projections.Album).ToListAsync(ct);
            notYetPlayed = notYetPlayedIds.Select(id => notYetPlayed.FirstOrDefault(a => a.Id == id)).OfType<AlbumDto>().ToList();
            var newReleases = await ReleaseEndpoints.HomeAsync(db, http.User.CanRequest(), time, ct);
            return new HomeDto(recentAlbums, recentTracks, playlists, rediscover, notYetPlayed, newReleases);
        });

        g.MapGet("/artists", async (string? q, int? offset, int? limit, SonNuageDbContext db, CancellationToken ct) =>
        {
            // Seulement les artistes qui ont un album : un crédit de titre (« X feat. Y ») n'est pas un artiste de la bibliothèque.
            var query = db.Artists.Where(a => a.Albums.Any());
            if (!string.IsNullOrWhiteSpace(q))
            {
                var key = NameNormalizer.Key(q);
                query = query.Where(a => a.NormalizedName.Contains(key));
            }
            return await PageAsync(query.OrderBy(a => a.SortName).ThenBy(a => a.Id).Select(Projections.Artist), offset, limit, ct);
        });

        g.MapGet("/artists/{id:guid}", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var artist = await db.Artists.Where(a => a.Id == id).Select(Projections.Artist).FirstOrDefaultAsync(ct);
            if (artist is null) return Results.NotFound();
            var albums = await db.Albums.Where(a => a.ArtistId == id).OrderBy(a => a.Year).ThenBy(a => a.Title)
                .Select(Projections.Album).ToListAsync(ct);
            var favorite = await db.FavoriteArtists.AnyAsync(f => f.UserId == userId && f.ArtistId == id, ct);
            return Results.Ok(new ArtistDetailDto(artist, albums, favorite, await UserRatings.OfAsync(db, userId, id, ct)));
        });

        g.MapGet("/artists/{id:guid}/tracks", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await db.Tracks.Where(t => t.Album.ArtistId == id || t.ArtistId == id)
                .OrderBy(t => t.Album.Year).ThenBy(t => t.Album.Title).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct));

        // « Apparaît sur » : les morceaux où l'artiste est invité.
        g.MapGet("/artists/{id:guid}/appearances", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await db.Tracks.Where(t => db.TrackGuests.Any(g => g.TrackId == t.Id && g.ArtistId == id))
                .OrderBy(t => t.Album.Year).ThenBy(t => t.Album.Title).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct));

        g.MapGet("/albums", async (string? sort, string? q, int? offset, int? limit, SonNuageDbContext db, CancellationToken ct) =>
        {
            var query = db.Albums.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var key = NameNormalizer.Key(q);
                query = query.Where(a => a.NormalizedTitle.Contains(key) || a.Artist.NormalizedName.Contains(key));
            }
            query = sort switch
            {
                "recent" => query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id),
                "year" => query.OrderByDescending(a => a.Year).ThenBy(a => a.Title).ThenBy(a => a.Id),
                "artist" => query.OrderBy(a => a.Artist.SortName).ThenBy(a => a.Year).ThenBy(a => a.Id),
                _ => query.OrderBy(a => a.Title).ThenBy(a => a.Id),
            };
            return await PageAsync(query.Select(Projections.Album), offset, limit, ct);
        });

        g.MapGet("/albums/{id:guid}", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var album = await db.Albums.Where(a => a.Id == id).Select(Projections.Album).FirstOrDefaultAsync(ct);
            if (album is null) return Results.NotFound();
            var userId = http.User.UserId();
            var tracks = await db.Tracks.Where(t => t.AlbumId == id)
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title)
                .Select(Projections.Track(db, userId)).ToListAsync(ct);
            var favorite = await db.FavoriteAlbums.AnyAsync(f => f.UserId == userId && f.AlbumId == id, ct);
            return Results.Ok(new AlbumDetailDto(album, tracks, favorite, await UserRatings.OfAsync(db, userId, id, ct)));
        });

        g.MapGet("/tracks/{id:guid}", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await db.Tracks.Where(t => t.Id == id).Select(Projections.Track(db, http.User.UserId())).FirstOrDefaultAsync(ct)
                is { } t ? Results.Ok(t) : Results.NotFound());

        // Résolution groupée : restauration d'une file d'attente, Android Auto.
        g.MapPost("/tracks/lookup", async (List<Guid> ids, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await TracksInOrderAsync(db, http.User.UserId(), ids.Take(1000).ToList(), ct));

        // Mélange de toute la bibliothèque (« Radio », lecture aléatoire globale).
        g.MapGet("/tracks/random", async (int? count, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await db.Tracks.OrderBy(_ => EF.Functions.Random()).Take(Math.Clamp(count ?? 50, 1, 500))
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct));

        g.MapGet("/search", (string q, int? limit, HttpContext http, SearchService search, CancellationToken ct) =>
            search.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), http.User.UserId(), ct));

        // Recherche par ambiance (« musique calme pour travailler ») : 204 quand la demande n'en est pas une.
        g.MapGet("/search/ambiance", async (string q, int? limit, HttpContext http, SearchService search, CancellationToken ct) =>
            await search.AmbianceAsync(q, Math.Clamp(limit ?? 20, 1, 100), http.User.UserId(), ct) is { } found
                ? Results.Ok(found)
                : Results.NoContent());

        g.MapGet("/search/suggest",(string q, int? limit, SearchService search, CancellationToken ct) =>
            search.SuggestAsync(q, Math.Clamp(limit ?? 8, 1, 20), ct));

        app.MapMethods("/api/albums/{id:guid}/cover", ["GET", "HEAD"], async (Guid id, int? size, HttpContext http, SonNuageDbContext db, CoverService covers, CancellationToken ct) =>
        {
            var album = await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (album is null) return Results.NotFound();
            var cover = await covers.GetAsync(db, album, size, ct);
            if (cover is null) return Results.NotFound();

            http.Response.Headers.CacheControl = "private, max-age=604800";
            var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{album.Id:N}-{album.CoverVersion}-{size}\"");
            return Results.File(cover.Value.Path, cover.Value.ContentType, entityTag: etag, enableRangeProcessing: false);
        }).RequireAuthorization(AuthConstants.MediaPolicy).RequireCors(DesktopCors.Policy).WithTags("Media");
    }

    internal static async Task<List<Guid>> RecentTrackIdsAsync(SonNuageDbContext db, Guid userId, int count, CancellationToken ct)
    {
        var recent = await db.PlayEvents.Where(p => p.UserId == userId)
            .OrderByDescending(p => p.PlayedAt).Take(count * 5)
            .Select(p => p.TrackId).ToListAsync(ct);
        return recent.Distinct().Take(count).ToList();
    }

    internal static async Task<List<TrackDto>> TracksInOrderAsync(SonNuageDbContext db, Guid userId, IReadOnlyList<Guid> ids, CancellationToken ct)
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
