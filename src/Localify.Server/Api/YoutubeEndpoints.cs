using Localify.Contracts.Localization;
using System.Text.Json;
using Localify.Contracts;
using Localify.Server.Auth;
using Localify.Server.Data;
using Localify.Server.Library;
using Localify.Server.Youtube;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Api;

/// <summary>Import depuis YouTube : réservé aux administrateurs (recherche, file, vérification des métadonnées, abonnements, outils).</summary>
public static class YoutubeEndpoints
{
    public static void MapYoutubeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/youtube").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("YouTube");
        // Seules routes ouvertes aussi aux clés d'extension de navigateur (ajouter, jamais lire ni supprimer).
        var ext = app.MapGroup("/api/admin/youtube").RequireAuthorization(AuthConstants.ExtensionPolicy).WithTags("YouTube");

        g.MapGet("/status", async (bool? refresh, LocalifyDbContext db, YoutubeTools tools, YoutubeSettingsStore settings, YoutubeImporter importer, CancellationToken ct) =>
        {
            var counts = await db.YoutubeItems.GroupBy(i => new { i.Status, i.NeedsReview }).Select(x => new { x.Key.Status, x.Key.NeedsReview, Count = x.Count() }).ToListAsync(ct);
            int Count(params YoutubeItemStatus[] s) => counts.Where(c => s.Contains(c.Status)).Sum(c => c.Count);
            return new YoutubeStatusDto(
                await tools.StatusAsync(refresh == true),
                settings.CookiesPath is not null,
                settings.CookiesUpdatedAt,
                importer.HoldUntil,
                importer.HoldReason,
                Count(YoutubeItemStatus.Queued, YoutubeItemStatus.Downloading, YoutubeItemStatus.Downloaded, YoutubeItemStatus.Processing),
                Count(YoutubeItemStatus.Failed),
                counts.Where(c => c.Status == YoutubeItemStatus.Done && c.NeedsReview).Sum(c => c.Count),
                Count(YoutubeItemStatus.Done),
                importer.Active);
        });

        g.MapPost("/resume", (YoutubeImporter importer) =>
        {
            importer.Resume();
            return Results.NoContent();
        });

        g.MapGet("/settings", (YoutubeSettingsStore settings) => settings.Settings);
        g.MapPut("/settings", (YoutubeSettingsDto req, YoutubeSettingsStore settings) =>
        {
            var (saved, error) = settings.Save(req);
            return saved is null ? Results.Problem(error, statusCode: 400) : Results.Ok(saved);
        });

        ext.MapPut("/cookies", (YoutubeCookiesRequest req, YoutubeSettingsStore settings) =>
            settings.SaveCookies(req.Content ?? "") is { } error ? Results.Problem(error, statusCode: 400) : Results.NoContent());
        g.MapDelete("/cookies", (YoutubeSettingsStore settings) =>
        {
            settings.DeleteCookies();
            return Results.NoContent();
        });

        g.MapPost("/tools/update", async (bool? deno, YoutubeTools tools, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await tools.UpdateAsync(deno == true, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Results.Problem(Tx.Key("errors.yt.updateFailed", ("message", ex.Message)), statusCode: 502);
            }
        });

        g.MapGet("/search", async (string q, bool? music, int? limit, YtDlp ytDlp, LocalifyDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest();
            // Une adresse collée dans la recherche est lue directement.
            if (YoutubeSyncService.NormalizeUrl(q) is { } url) return await ListingAsync(ytDlp, db, url, 500, ct);
            return await Run(async () => await WithStatusAsync(db, await ytDlp.SearchAsync(q.Trim(), music == true, Math.Clamp(limit ?? 20, 1, 50), ct), q, ct));
        });

        ext.MapGet("/resolve", async (string url, YtDlp ytDlp, LocalifyDbContext db, CancellationToken ct) =>
            YoutubeSyncService.NormalizeUrl(url) is { } u ? await ListingAsync(ytDlp, db, u, 1000, ct) : Results.Problem("Adresse YouTube invalide.", statusCode: 400));

        ext.MapPost("/imports", async (YoutubeImportRequest req, YoutubeImporter importer, LocalifyDbContext db, CancellationToken ct) =>
            req.Videos is not { Count: > 0 and <= 1000 }
                ? Results.Problem(Tx.Key("errors.yt.batchSize"), statusCode: 400)
                : Results.Ok(await importer.EnqueueAsync(db, req.Videos, string.IsNullOrWhiteSpace(req.AlbumHint) ? null : req.AlbumHint.Trim(), null, ct)));

        MapItems(g);
        MapSubscriptions(g, ext);
    }

    private static void MapItems(RouteGroupBuilder g)
    {
        g.MapGet("/items", async (string? status, bool? review, string? q, Guid? subscriptionId, int? offset, int? limit, LocalifyDbContext db, CancellationToken ct) =>
        {
            var query = db.YoutubeItems.AsQueryable();
            if (Enum.TryParse<YoutubeItemStatus>(status, true, out var s))
            {
                query = s is YoutubeItemStatus.Queued
                    ? query.Where(i => i.Status == YoutubeItemStatus.Queued || i.Status == YoutubeItemStatus.Downloading || i.Status == YoutubeItemStatus.Downloaded || i.Status == YoutubeItemStatus.Processing)
                    : query.Where(i => i.Status == s);
            }
            if (review == true) query = query.Where(i => i.NeedsReview && i.Status == YoutubeItemStatus.Done);
            if (subscriptionId is { } sid) query = query.Where(i => i.SubscriptionId == sid);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(i => EF.Functions.Like(i.Title, $"%{term}%") || EF.Functions.Like(i.Channel!, $"%{term}%") || i.VideoId == term);
            }
            var o = Math.Max(0, offset ?? 0);
            var l = Math.Clamp(limit ?? 50, 1, 200);
            var total = await query.CountAsync(ct);
            // Les imports en cours d'abord, puis les plus récents.
            var rows = await query
                .OrderBy(i => i.Status == YoutubeItemStatus.Downloading || i.Status == YoutubeItemStatus.Downloaded || i.Status == YoutubeItemStatus.Processing ? 0 : i.Status == YoutubeItemStatus.Queued ? 1 : 2)
                .ThenByDescending(i => i.Status == YoutubeItemStatus.Queued ? DateTimeOffset.MinValue : i.UpdatedAt)
                .ThenBy(i => i.CreatedAt)
                .Skip(o).Take(l)
                .Select(i => new YoutubeItemDto(i.Id, i.VideoId, i.Title, i.Channel, i.DurationSeconds, i.ThumbnailUrl, i.Status.ToString(), i.Error, i.Attempts,
                    i.TrackId, i.Track != null ? i.Track.Title : null, i.Track != null ? i.Track.ArtistName : null, i.Track != null ? i.Track.AlbumTitle : null,
                    i.Track != null ? i.Track.AlbumId : null, i.NeedsReview, i.MatchConfidence, i.MatchSource,
                    i.Subscription != null ? i.Subscription.Title : null, i.CreatedAt, i.CompletedAt))
                .ToListAsync(ct);
            return new PagedResult<YoutubeItemDto>(rows, total, o, l);
        });

        // format : sélecteur yt-dlp pour cette seule tentative (après un « format indisponible »).
        g.MapPost("/items/{id:guid}/retry", async (Guid id, string? format, LocalifyDbContext db, YoutubeImporter importer, CancellationToken ct) =>
        {
            var item = await db.YoutubeItems.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (item.Status is not (YoutubeItemStatus.Failed or YoutubeItemStatus.Skipped)) return Results.Problem(Tx.Key("errors.yt.retryOnlyFailed"), statusCode: 409);
            if (!string.IsNullOrWhiteSpace(format))
            {
                if (!YtDlp.IsValidFormat(format.Trim())) return Results.Problem(Tx.Key("errors.yt.badFormat"), statusCode: 400);
                importer.UseFormatOnce(item.Id, format.Trim());
            }
            item.Status = YoutubeItemStatus.Queued;
            item.Error = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            importer.Wake();
            return Results.NoContent();
        });

        g.MapGet("/items/{id:guid}/formats", async (Guid id, LocalifyDbContext db, YoutubeImporter importer, CancellationToken ct) =>
        {
            var item = await db.YoutubeItems.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            var path = importer.FormatsPath(item.VideoId);
            return File.Exists(path) ? Results.Text(await File.ReadAllTextAsync(path, ct)) : Results.NoContent();
        });

        g.MapPost("/items/retry-failed", async (LocalifyDbContext db, YoutubeImporter importer, CancellationToken ct) =>
        {
            var n = await db.YoutubeItems.Where(i => i.Status == YoutubeItemStatus.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, YoutubeItemStatus.Queued).SetProperty(i => i.Error, (string?)null), ct);
            importer.Wake();
            return Results.Ok(n);
        });

        g.MapDelete("/items/{id:guid}", async (Guid id, bool? deleteTrack, LocalifyDbContext db, YoutubeLibraryWriter writer, IOptions<LocalifyOptions> options, CancellationToken ct) =>
        {
            var item = await db.YoutubeItems.Include(i => i.Track).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (item.Status is YoutubeItemStatus.Downloading or YoutubeItemStatus.Downloaded or YoutubeItemStatus.Processing) return Results.Problem(Tx.Key("errors.yt.inProgress"), statusCode: 409);
            if (deleteTrack == true && item.Track is { } track && LibraryScanner.IsUnder(track.FilePath, Path.GetFullPath(options.Value.UploadPath)))
            {
                if (File.Exists(track.FilePath)) File.Delete(track.FilePath);
                writer.RemoveIfEmpty(Path.GetDirectoryName(track.FilePath)!);
                db.Tracks.Remove(track);
            }
            // Une vidéo d'abonnement reste connue (ignorée) pour ne pas revenir à la synchronisation suivante.
            if (item.SubscriptionId is not null)
            {
                item.Status = YoutubeItemStatus.Skipped;
                item.TrackId = null;
                item.NeedsReview = false;
            }
            else
            {
                db.YoutubeItems.Remove(item);
            }
            await db.SaveChangesAsync(ct);
            if (deleteTrack == true) await LibraryIndexer.PruneAsync(db, ct);
            return Results.NoContent();
        });

        g.MapGet("/items/{id:guid}/candidates", async (Guid id, LocalifyDbContext db, CancellationToken ct) =>
        {
            var json = await db.YoutubeItems.Where(i => i.Id == id).Select(i => i.CandidatesJson).FirstOrDefaultAsync(ct);
            return json is null ? [] : JsonSerializer.Deserialize<List<MetadataCandidateDto>>(json, YoutubeImporter.Json) ?? [];
        });

        g.MapPost("/items/{id:guid}/apply", async (Guid id, MetadataCandidateDto candidate, YoutubeImporter importer, LocalifyDbContext db, CancellationToken ct) =>
            await importer.ApplyAsync(db, id, candidate, ct) is { } error ? Results.Problem(error, statusCode: 400) : Results.NoContent());

        g.MapPost("/items/{id:guid}/accept", async (Guid id, LocalifyDbContext db, CancellationToken ct) =>
        {
            var n = await db.YoutubeItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.NeedsReview, false), ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Recherche MusicBrainz libre depuis la vérification d'un import (quand aucun candidat ne convient).
        g.MapGet("/musicbrainz", async (string title, string? artist, string? album, double? duration, MusicBrainzClient mb, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest();
            return await Run(async () =>
            {
                var recordings = await mb.SearchRecordingsAsync(title.Trim(), string.IsNullOrWhiteSpace(artist) ? null : artist.Trim(), ct);
                return recordings
                    .Select(r => MetadataResolver.ToCandidate(r, album, MetadataResolver.TextScore(r, title, artist, duration), "musicbrainz"))
                    .OrderByDescending(c => c.Score)
                    .Select(c => c with { Score = Math.Round(c.Score, 3) })
                    .ToList();
            });
        });
    }

    private static void MapSubscriptions(RouteGroupBuilder g, RouteGroupBuilder ext)
    {
        g.MapGet("/subscriptions", async (LocalifyDbContext db, CancellationToken ct) =>
            await db.YoutubeSubscriptions.OrderBy(s => s.Title)
                .Select(s => new YoutubeSubscriptionDto(s.Id, s.Url, s.Title, s.Enabled, s.IsAlbum, s.CreatedAt, s.LastSyncedAt, s.LastError,
                    db.YoutubeItems.Count(i => i.SubscriptionId == s.Id && i.Status != YoutubeItemStatus.Skipped),
                    db.YoutubeItems.Count(i => i.SubscriptionId == s.Id && i.Status == YoutubeItemStatus.Done)))
                .ToListAsync(ct));

        ext.MapPost("/subscriptions", async (CreateSubscriptionRequest req, YoutubeSyncService sync, LocalifyDbContext db, CancellationToken ct) =>
        {
            var (sub, error) = await sync.CreateAsync(db, req, ct);
            return sub is null ? Results.Problem(error, statusCode: 400) : Results.Created($"/api/admin/youtube/subscriptions/{sub.Id}", sub.Id);
        });

        g.MapPut("/subscriptions/{id:guid}", async (Guid id, UpdateSubscriptionRequest req, LocalifyDbContext db, CancellationToken ct) =>
        {
            var sub = await db.YoutubeSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (sub is null) return Results.NotFound();
            sub.Enabled = req.Enabled ?? sub.Enabled;
            sub.IsAlbum = req.IsAlbum ?? sub.IsAlbum;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Les morceaux déjà importés restent dans la bibliothèque.
        g.MapDelete("/subscriptions/{id:guid}", async (Guid id, LocalifyDbContext db, CancellationToken ct) =>
        {
            await db.YoutubeItems.Where(i => i.SubscriptionId == id && (i.Status == YoutubeItemStatus.Skipped || i.Status == YoutubeItemStatus.Queued)).ExecuteDeleteAsync(ct);
            return await db.YoutubeSubscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        g.MapPost("/subscriptions/{id:guid}/sync", async (Guid id, YoutubeSyncService sync, CancellationToken ct) =>
            await sync.SyncAsync(id, ct) is { } error ? Results.Problem(error, statusCode: 502) : Results.NoContent());
    }

    private static Task<IResult> ListingAsync(YtDlp ytDlp, LocalifyDbContext db, string url, int limit, CancellationToken ct) =>
        Run(async () => await WithStatusAsync(db, await ytDlp.ListAsync(url, limit, ct), url, ct));

    private static async Task<YoutubeListingDto> WithStatusAsync(LocalifyDbContext db, YtListing listing, string url, CancellationToken ct)
    {
        var ids = listing.Entries.Select(e => e.Id).ToList();
        var known = await db.YoutubeItems.Where(i => ids.Contains(i.VideoId)).ToDictionaryAsync(i => i.VideoId, i => i.Status.ToString(), ct);
        return new YoutubeListingDto(listing.Kind, listing.Title, listing.Channel, url,
            listing.Entries.Select(e => new YoutubeVideoDto(e.Id, e.Title, e.Channel, e.DurationSeconds, e.Thumbnail, known.GetValueOrDefault(e.Id), e.Album)).ToList());
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (YtDlpException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.Kind == YtDlpFailure.Throttled ? 429 : 502);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(Tx.Key("errors.yt.unreachable", ("message", ex.Message)), statusCode: 502);
        }
    }
}
