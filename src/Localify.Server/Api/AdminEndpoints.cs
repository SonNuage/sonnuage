using Localify.Contracts;
using Localify.Server.Admin;
using Localify.Server.Auth;
using Localify.Server.Data;
using Localify.Server.Library;
using Localify.Server.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Api;

public static class AdminEndpoints
{
    private const long MaxCoverBytes = 10 * 1024 * 1024;

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");

        g.MapGet("/stats", async (LocalifyDbContext db, ScanCoordinator scans, TranscodeService transcoder, CancellationToken ct) =>
            new LibraryStatsDto(
                await db.Artists.CountAsync(ct),
                await db.Albums.CountAsync(ct),
                await db.Tracks.CountAsync(ct),
                await db.Tracks.SumAsync(t => t.FileSize, ct),
                await db.Tracks.SumAsync(t => t.DurationSeconds, ct),
                transcoder.CacheSizeBytes(),
                scans.Status));

        g.MapPost("/scan", (ScanCoordinator scans) =>
        {
            scans.RequestFullScan();
            return Results.Accepted();
        });

        g.MapDelete("/cache/transcode", (TranscodeService transcoder) =>
        {
            transcoder.ClearCache();
            return Results.NoContent();
        });

        MapTracks(g);
        MapAlbums(g);
        MapUploads(g);
        MapUsers(g);
    }

    private static void MapTracks(RouteGroupBuilder g)
    {
        g.MapGet("/tracks", async (string? q, Guid? albumId, int? offset, int? limit, LocalifyDbContext db, IOptions<LocalifyOptions> options, CancellationToken ct) =>
        {
            var query = db.Tracks.AsQueryable();
            if (albumId is { } a) query = query.Where(t => t.AlbumId == a);
            if (!string.IsNullOrWhiteSpace(q))
            {
                foreach (var token in q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NameNormalizer.Key))
                    query = query.Where(t => t.SearchKey.Contains(token));
            }
            var o = Math.Max(0, offset ?? 0);
            var l = Math.Clamp(limit ?? 100, 1, 500);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(t => t.AlbumArtistName).ThenBy(t => t.AlbumTitle).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                .Skip(o).Take(l).ToListAsync(ct);
            var roots = RootsOf(options.Value);
            return new PagedResult<TrackMetadataDto>(rows.Select(t => ToMetadataDto(t, roots, options.Value)).ToList(), total, o, l);
        });

        g.MapPut("/tracks/{id:guid}/metadata", async (Guid id, UpdateTrackMetadataRequest req, LocalifyDbContext db, CoverLocator covers, IOptions<LocalifyOptions> options, CancellationToken ct) =>
        {
            var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (track is null) return Results.NotFound();
            if (Validate(req.Title) is { } e1) return e1;

            track.Title = Clean(req.Title) ?? track.Title;
            track.ArtistName = Clean(req.ArtistName) ?? track.ArtistName;
            track.AlbumTitle = Clean(req.AlbumTitle) ?? track.AlbumTitle;
            track.AlbumArtistName = Clean(req.AlbumArtistName) ?? track.AlbumArtistName;
            track.TrackNumber = req.TrackNumber;
            track.DiscNumber = req.DiscNumber;
            track.Year = req.Year;
            track.Genre = Clean(req.Genre);
            track.MetadataLocked = true;

            await ReassignAsync(db, covers, [track], ct);
            return Results.Ok(ToMetadataDto(track, RootsOf(options.Value), options.Value));
        });

        g.MapDelete("/tracks/{id:guid}", async (Guid id, LocalifyDbContext db, IOptions<LocalifyOptions> options, CancellationToken ct) =>
        {
            var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (track is null) return Results.NotFound();
            // Seuls les fichiers envoyés via l'admin sont supprimables : la bibliothèque principale reste en lecture seule.
            var uploadRoot = Path.GetFullPath(options.Value.UploadPath);
            if (!LibraryScanner.IsUnder(track.FilePath, uploadRoot))
                return Results.Problem("Ce fichier fait partie de la bibliothèque principale (lecture seule). Supprimez-le directement sur le disque.", statusCode: 409);

            if (File.Exists(track.FilePath)) File.Delete(track.FilePath);
            db.Tracks.Remove(track);
            await db.SaveChangesAsync(ct);
            await LibraryIndexer.PruneAsync(db, ct);
            return Results.NoContent();
        });
    }

    private static void MapAlbums(RouteGroupBuilder g)
    {
        g.MapPut("/albums/{id:guid}", async (Guid id, UpdateAlbumRequest req, LocalifyDbContext db, CoverLocator covers, CancellationToken ct) =>
        {
            var tracks = await db.Tracks.Where(t => t.AlbumId == id).ToListAsync(ct);
            if (tracks.Count == 0) return Results.NotFound();
            if (Validate(req.Title) is { } e1) return e1;

            foreach (var t in tracks)
            {
                if (Clean(req.Title) is { } title) t.AlbumTitle = title;
                if (Clean(req.ArtistName) is { } artist) t.AlbumArtistName = artist;
                if (req.Year is not null) t.Year = req.Year;
                t.MetadataLocked = true;
            }
            await ReassignAsync(db, covers, tracks, ct);
            var newAlbumId = tracks[0].AlbumId;
            if (req.Year is not null) await db.Albums.Where(a => a.Id == newAlbumId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Year, req.Year), ct);
            return Results.Ok(await db.Albums.Where(a => a.Id == newAlbumId).Select(Projections.Album).FirstAsync(ct));
        });

        g.MapPut("/albums/{id:guid}/cover", async (Guid id, HttpRequest request, LocalifyDbContext db, IOptions<LocalifyOptions> options, CancellationToken ct) =>
        {
            var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (album is null) return Results.NotFound();
            var ext = request.ContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => null,
            };
            if (ext is null) return Results.Problem("Image JPEG, PNG ou WebP attendue.", statusCode: 415);

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length is 0 or > MaxCoverBytes) return Results.Problem("Image vide ou trop lourde (10 Mo max).", statusCode: 400);

            var dir = CoverLocator.CustomCoverDirectory(options.Value);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.EnumerateFiles(dir, $"{id:N}.*")) File.Delete(old);
            var path = Path.Combine(dir, $"{id:N}{ext}");
            await File.WriteAllBytesAsync(path, buffer.ToArray(), ct);

            album.CoverPath = path;
            album.HasEmbeddedCover = false;
            album.CoverVersion++;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapUploads(RouteGroupBuilder g)
    {
        g.MapPost("/uploads", (StartUploadRequest req, UploadService uploads) =>
        {
            var (session, error) = uploads.Start(req);
            return error is null ? Results.Ok(session) : Results.Problem(error, statusCode: 400);
        });

        g.MapGet("/uploads/{id:guid}", (Guid id, UploadService uploads) =>
            uploads.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());

        g.MapPut("/uploads/{id:guid}", async (Guid id, long offset, HttpRequest request, UploadService uploads, CancellationToken ct) =>
        {
            var (session, error) = await uploads.AppendAsync(id, offset, request.Body, ct);
            if (session is null) return Results.NotFound();
            return error is null ? Results.Ok(session) : Results.Conflict(session);
        }).DisableAntiforgery();

        g.MapPost("/uploads/{id:guid}/complete", async (Guid id, UploadService uploads, ScanCoordinator scans, LocalifyDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var (path, error) = uploads.Complete(id);
            if (path is null) return Results.Problem(error, statusCode: 400);
            var tracks = await scans.ScanFilesNowAsync([path], ct);
            if (tracks.Count == 0) return Results.Problem("Fichier reçu mais illisible.", statusCode: 422);
            var ids = tracks.Select(t => t.Id).ToList();
            return Results.Ok(await LibraryEndpoints.TracksInOrderAsync(db, http.User.UserId(), ids, ct));
        });

        g.MapDelete("/uploads/{id:guid}", (Guid id, UploadService uploads) =>
        {
            uploads.Cancel(id);
            return Results.NoContent();
        });
    }

    private static void MapUsers(RouteGroupBuilder g)
    {
        g.MapGet("/users", async (LocalifyDbContext db, CancellationToken ct) =>
            (await db.Users.OrderBy(u => u.Username).ToListAsync(ct)).Select(u => u.ToDto()));

        g.MapPost("/users", async (CreateUserRequest req, UserService users, CancellationToken ct) =>
        {
            var (user, error) = await users.CreateAsync(req.Username ?? "", req.Password ?? "", req.Role ?? Roles.User, ct);
            return user is null ? Results.Problem(error, statusCode: 400) : Results.Ok(user.ToDto());
        });

        g.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserRequest req, HttpContext http, LocalifyDbContext db, UserService users, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();
            if (req.Role is { } role)
            {
                if (role is not (Roles.Admin or Roles.User)) return Results.Problem("Rôle inconnu.", statusCode: 400);
                if (role != Roles.Admin && user.Role == Roles.Admin && await db.Users.CountAsync(u => u.Role == Roles.Admin, ct) == 1)
                    return Results.Problem("Il faut garder au moins un administrateur.", statusCode: 409);
                if (user.Role != role)
                {
                    user.Role = role;
                    user.SecurityStamp = Guid.NewGuid().ToString("N");
                }
            }
            if (req.Password is { } password)
            {
                if (password.Length < UserService.MinPasswordLength)
                    return Results.Problem($"Mot de passe : {UserService.MinPasswordLength} caractères minimum.", statusCode: 400);
                await users.SetPasswordAsync(user, password, ct);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(user.ToDto());
        });

        g.MapDelete("/users/{id:guid}", async (Guid id, HttpContext http, LocalifyDbContext db, CancellationToken ct) =>
        {
            if (id == http.User.UserId()) return Results.Problem("Impossible de supprimer son propre compte.", statusCode: 409);
            var deleted = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static async Task ReassignAsync(LocalifyDbContext db, CoverLocator covers, IReadOnlyList<Track> tracks, CancellationToken ct)
    {
        var indexer = new LibraryIndexer(db);
        await indexer.LoadAsync(ct);
        foreach (var t in tracks)
        {
            indexer.TouchedAlbums.Add(t.AlbumId);
            indexer.Assign(t);
        }
        await db.SaveChangesAsync(ct);
        await LibraryIndexer.PruneAsync(db, ct);
        await covers.RefreshAsync(db, indexer.TouchedAlbums, ct);
    }

    private static IResult? Validate(string? title) =>
        title is not null && string.IsNullOrWhiteSpace(title) ? Results.Problem("Le titre ne peut pas être vide.", statusCode: 400) : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string> RootsOf(LocalifyOptions o) =>
        o.MusicPaths.Append(o.UploadPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToList();

    private static TrackMetadataDto ToMetadataDto(Track t, List<string> roots, LocalifyOptions options)
    {
        var root = roots.FirstOrDefault(r => LibraryScanner.IsUnder(t.FilePath, r));
        var relative = root is null ? Path.GetFileName(t.FilePath) : Path.Combine(Path.GetFileName(root), Path.GetRelativePath(root, t.FilePath));
        return new TrackMetadataDto(t.Id, relative.Replace('\\', '/'), t.Title, t.ArtistName, t.AlbumTitle, t.AlbumArtistName,
            t.TrackNumber, t.DiscNumber, t.Year, t.Genre, t.MetadataLocked,
            LibraryScanner.IsUnder(t.FilePath, Path.GetFullPath(options.UploadPath)));
    }
}
