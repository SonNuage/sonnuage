using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Api;

/// <summary>
/// Demandes de téléchargement. Un contributeur cherche sur YouTube et demande une vidéo, dans la limite de son quota
/// sur 24 heures. Un admin l'accepte (elle rejoint la file d'import YouTube) ou la refuse ; l'approbation peut être
/// automatique pour un compte. Seuls des identifiants de vidéo YouTube sont acceptés.
/// </summary>
public static class DownloadRequestEndpoints
{
    private const int DefaultPage = 50;
    private const int MaxPage = 200;

    public static void MapDownloadRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/requests").RequireAuthorization(AuthConstants.ContributorPolicy).WithTags("Requests");

        mine.MapGet("/search", async (string q, bool? music, YtDlp ytDlp, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest();
            if (YoutubeSyncService.NormalizeUrl(q) is { } url) return await YoutubeEndpoints.ListingAsync(ytDlp, db, url, MaxPage, ct);
            return await YoutubeEndpoints.Run(async () =>
                await YoutubeEndpoints.WithStatusAsync(db, await ytDlp.SearchAsync(q.Trim(), music == true, 20, ct), q, ct));
        });

        mine.MapPost("", async (CreateDownloadRequest req, HttpContext http, SonNuageDbContext db, YoutubeImporter importer, TimeProvider time, CancellationToken ct) =>
        {
            if (VideoIdOf(req.Video) is not { } videoId) return Results.Problem(Tx.Key("errors.requests.badVideo"), statusCode: 400);
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            if (user is null) return Results.Unauthorized();
            var now = time.GetUtcNow();
            if (user.Role != Roles.Admin && await UsedAsync(db, user.Id, now, ct) >= user.RequestsPerDay)
                return Results.Problem(Tx.Key("errors.requests.quota", ("limit", user.RequestsPerDay)), statusCode: 429);
            if (await db.YoutubeItems.AnyAsync(i => i.VideoId == videoId && i.Status != YoutubeItemStatus.Failed && i.Status != YoutubeItemStatus.Skipped, ct))
                return Results.Problem(Tx.Key("errors.requests.alreadyKnown"), statusCode: 409);
            if (await db.DownloadRequests.AnyAsync(r => r.VideoId == videoId && r.Decision == DownloadRequestDecision.Requested, ct))
                return Results.Problem(Tx.Key("errors.requests.duplicate"), statusCode: 409);

            var request = new DownloadRequest
            {
                UserId = user.Id,
                User = user,
                VideoId = videoId,
                Title = Clean(req.Title, 500) ?? videoId,
                Channel = Clean(req.Channel, 200),
                DurationSeconds = req.DurationSeconds is > 0 and < 86_400 ? req.DurationSeconds : null,
                Note = Clean(req.Note, 500),
                CreatedAt = now,
            };
            db.DownloadRequests.Add(request);
            if (user.Role == Roles.Admin || user.AutoApproveRequests) await AcceptAsync(db, importer, request, null, now, ct);
            else await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(request));
        });

        mine.MapGet("/mine", async (int? offset, int? limit, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await PageAsync(db.DownloadRequests.Where(r => r.UserId == http.User.UserId()), offset, limit, ct));

        mine.MapGet("/quota", async (HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            if (user is null) return Results.Unauthorized();
            var isAdmin = user.Role == Roles.Admin;
            return Results.Ok(new RequestQuotaDto(await UsedAsync(db, user.Id, time.GetUtcNow(), ct), isAdmin ? null : user.RequestsPerDay,
                isAdmin || user.AutoApproveRequests));
        });

        var admin = app.MapGroup("/api/admin/requests").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");

        admin.MapGet("", async (string? status, string? q, Guid? userId, int? offset, int? limit, SonNuageDbContext db, CancellationToken ct) =>
        {
            var query = db.DownloadRequests.AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!DownloadRequestStatus.All.Contains(status)) return Results.BadRequest();
                query = WithStatus(query, status);
            }
            if (userId is { } uid) query = query.Where(r => r.UserId == uid);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(r => EF.Functions.Like(r.Title, like) || EF.Functions.Like(r.Channel!, like) || EF.Functions.Like(r.User.Username, like));
            }
            return Results.Ok(await PageAsync(query, offset, limit, ct));
        });

        admin.MapPost("/{id:guid}/approve", async (Guid id, HttpContext http, SonNuageDbContext db, YoutubeImporter importer, TimeProvider time, CancellationToken ct) =>
        {
            var request = await LoadAsync(db, id, ct);
            if (request is null) return Results.NotFound();
            if (request.Decision == DownloadRequestDecision.Accepted) return Results.Problem(Tx.Key("errors.requests.notPending"), statusCode: 409);
            await AcceptAsync(db, importer, request, await NameAsync(db, http, ct), time.GetUtcNow(), ct);
            return Results.Ok(ToDto(request));
        });

        admin.MapPost("/{id:guid}/deny", async (Guid id, DenyDownloadRequest req, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var request = await LoadAsync(db, id, ct);
            if (request is null) return Results.NotFound();
            if (request.Decision != DownloadRequestDecision.Requested) return Results.Problem(Tx.Key("errors.requests.notPending"), statusCode: 409);
            request.Decision = DownloadRequestDecision.Denied;
            request.DenyReason = Clean(req.Reason, 500);
            request.DecidedAt = time.GetUtcNow();
            request.DecidedBy = await NameAsync(db, http, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(request));
        });
    }

    /// <summary>Identifiant d'une vidéo YouTube, tapé tel quel ou tiré de son adresse ; null pour tout le reste.</summary>
    internal static string? VideoIdOf(string? input)
    {
        input = input?.Trim();
        if (YoutubeImporter.IsVideoId(input)) return input;
        if (YoutubeSyncService.NormalizeUrl(input) is not { } url) return null;
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = uri.Host == "youtu.be" ? segments.FirstOrDefault()
            : uri.AbsolutePath == "/watch" ? QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("v").ToString()
            : segments is ["shorts" or "live" or "embed", var s, ..] ? s
            : null;
        return YoutubeImporter.IsVideoId(id) ? id : null;
    }

    private static async Task AcceptAsync(SonNuageDbContext db, YoutubeImporter importer, DownloadRequest request, string? decidedBy, DateTimeOffset now, CancellationToken ct)
    {
        request.Decision = DownloadRequestDecision.Accepted;
        request.DecidedAt = now;
        request.DecidedBy = decidedBy;
        request.AutoApproved = decidedBy is null;
        request.DenyReason = null;
        // Une vidéo en échec est relancée ; une vidéo déjà connue n'est pas importée deux fois.
        await importer.EnqueueAsync(db, [new YoutubeVideoDto(request.VideoId, request.Title, request.Channel, request.DurationSeconds, Thumbnail(request.VideoId), null)],
            null, null, ct);
        request.YoutubeItem = await db.YoutubeItems.FirstOrDefaultAsync(i => i.VideoId == request.VideoId, ct);
        await db.SaveChangesAsync(ct);
    }

    private static Task<int> UsedAsync(SonNuageDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-1);
        return db.DownloadRequests.CountAsync(r => r.UserId == userId && r.CreatedAt > since, ct);
    }

    private static Task<DownloadRequest?> LoadAsync(SonNuageDbContext db, Guid id, CancellationToken ct) =>
        db.DownloadRequests.Include(r => r.User).Include(r => r.YoutubeItem).FirstOrDefaultAsync(r => r.Id == id, ct);

    private static async Task<string?> NameAsync(SonNuageDbContext db, HttpContext http, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == http.User.UserId()).Select(u => u.Username).FirstOrDefaultAsync(ct);

    private static async Task<PagedResult<DownloadRequestDto>> PageAsync(IQueryable<DownloadRequest> query, int? offset, int? limit, CancellationToken ct)
    {
        var skip = Math.Max(0, offset ?? 0);
        var take = Math.Clamp(limit ?? DefaultPage, 1, MaxPage);
        var total = await query.CountAsync(ct);
        var items = await query.Include(r => r.User).Include(r => r.YoutubeItem).AsNoTracking()
            .OrderByDescending(r => r.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return new PagedResult<DownloadRequestDto>(items.Select(ToDto).ToList(), total, skip, take);
    }

    /// <summary>Filtre sur l'état affiché : une demande acceptée suit l'avancement de son import.</summary>
    private static IQueryable<DownloadRequest> WithStatus(IQueryable<DownloadRequest> q, string status)
    {
        var accepted = q.Where(r => r.Decision == DownloadRequestDecision.Accepted);
        return status switch
        {
            DownloadRequestStatus.Requested => q.Where(r => r.Decision == DownloadRequestDecision.Requested),
            DownloadRequestStatus.Denied => q.Where(r => r.Decision == DownloadRequestDecision.Denied),
            DownloadRequestStatus.Accepted => accepted.Where(r => r.YoutubeItem == null || r.YoutubeItem.Status == YoutubeItemStatus.Queued),
            DownloadRequestStatus.InProgress => accepted.Where(r => r.YoutubeItem!.Status == YoutubeItemStatus.Downloading
                || r.YoutubeItem.Status == YoutubeItemStatus.Downloaded || r.YoutubeItem.Status == YoutubeItemStatus.Processing),
            DownloadRequestStatus.Done => accepted.Where(r => r.YoutubeItem!.Status == YoutubeItemStatus.Done),
            _ => accepted.Where(r => r.YoutubeItem!.Status == YoutubeItemStatus.Failed || r.YoutubeItem.Status == YoutubeItemStatus.Skipped),
        };
    }

    private static string StatusOf(DownloadRequest r) => r.Decision switch
    {
        DownloadRequestDecision.Requested => DownloadRequestStatus.Requested,
        DownloadRequestDecision.Denied => DownloadRequestStatus.Denied,
        _ => r.YoutubeItem?.Status switch
        {
            null or YoutubeItemStatus.Queued => DownloadRequestStatus.Accepted,
            YoutubeItemStatus.Done => DownloadRequestStatus.Done,
            YoutubeItemStatus.Failed or YoutubeItemStatus.Skipped => DownloadRequestStatus.Failed,
            _ => DownloadRequestStatus.InProgress,
        },
    };

    private static DownloadRequestDto ToDto(DownloadRequest r) => new(
        r.Id, r.VideoId, r.Title, r.Channel, r.DurationSeconds, Thumbnail(r.VideoId), r.Note, StatusOf(r), r.DenyReason,
        r.UserId, r.User.Username, r.CreatedAt, r.DecidedAt, r.DecidedBy, r.AutoApproved,
        r.YoutubeItem?.Status == YoutubeItemStatus.Done ? r.YoutubeItem.TrackId : null);

    // Calculée à partir de l'identifiant : une adresse d'image fournie par le client ne s'affiche jamais chez l'admin.
    private static string Thumbnail(string videoId) => $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
    }
}
