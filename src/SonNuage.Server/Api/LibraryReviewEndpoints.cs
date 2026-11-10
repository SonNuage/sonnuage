using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Api;

/// <summary>Revue de la bibliothèque dans l'admin : propositions des rescans AcoustID, doublons.</summary>
public static class LibraryReviewEndpoints
{
    /// <summary>Identifiants rendus d'un coup pour « tout sélectionner » (filtres compris).</summary>
    public const int MaxSelection = 5000;

    public static void MapLibraryReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/proposals").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");

        // status : pending (par défaut), accepted, rejected, superseded ou all ; score de 0 à 1.
        g.MapGet("", async ([AsParameters] ProposalFilter filter, int? offset, int? limit, SonNuageDbContext db, TagWriter tags, CancellationToken ct) =>
        {
            if (filter.Apply(db.RescanProposals) is not { } query) return Results.BadRequest();
            var o = Math.Max(0, offset ?? 0);
            var l = Math.Clamp(limit ?? 100, 1, 500);
            var total = await query.CountAsync(ct);
            var rows = await query.Include(p => p.Track).Include(p => p.Suite)
                .OrderByDescending(p => p.Score).ThenBy(p => p.Track.ArtistName).ThenBy(p => p.Track.Title)
                .Skip(o).Take(l).AsNoTracking().ToListAsync(ct);
            return Results.Ok(new PagedResult<RescanProposalDto>(rows.Select(p => ToDto(p, tags)).ToList(), total, o, l));
        });

        g.MapGet("/ids", async ([AsParameters] ProposalFilter filter, SonNuageDbContext db, CancellationToken ct) =>
            filter.Apply(db.RescanProposals) is { } query
                ? Results.Ok(await query.OrderByDescending(p => p.Score).Select(p => p.Id).Take(MaxSelection).ToListAsync(ct))
                : Results.BadRequest());

        g.MapGet("/suites", async (SonNuageDbContext db, CancellationToken ct) =>
            await db.JobSuites.Where(s => db.RescanProposals.Any(p => p.SuiteId == s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new ProposalSuiteDto(s.Id, s.Title, s.CreatedAt,
                    db.RescanProposals.Count(p => p.SuiteId == s.Id && p.Status == RescanProposalStatus.Pending)))
                .ToListAsync(ct));

        g.MapPost("/accept", async (DecideProposalsRequest req, HttpContext http, SonNuageDbContext db, RescanReview review, CancellationToken ct) =>
            Invalid(req) ?? Results.Ok(await review.AcceptAsync(db, req.Ids.Distinct().ToList(), await NameAsync(db, http, ct), ct)));

        g.MapPost("/reject", async (DecideProposalsRequest req, HttpContext http, SonNuageDbContext db, RescanReview review, CancellationToken ct) =>
            Invalid(req) ?? Results.Ok(await review.RejectAsync(db, req.Ids.Distinct().ToList(), await NameAsync(db, http, ct), ct)));

        MapDuplicates(app.MapGroup("/api/admin/duplicates").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin"));
    }

    private static void MapDuplicates(RouteGroupBuilder g)
    {
        g.MapGet("", async (SonNuageDbContext db, IFingerprinter fingerprinter, IOptions<SonNuageOptions> options, CancellationToken ct) =>
        {
            var groups = DuplicateFinder.Find(await DuplicateResolver.CandidatesAsync(db, null, ct));
            var ids = groups.SelectMany(g => g.Ids).ToList();
            var tracks = await db.Tracks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var plays = await CountsAsync(db.PlayEvents.Where(p => ids.Contains(p.TrackId)).GroupBy(p => p.TrackId), ct);
            var favorites = await CountsAsync(db.Favorites.Where(f => ids.Contains(f.TrackId)).GroupBy(f => f.TrackId), ct);
            var ratings = await CountsAsync(db.Ratings.Where(r => ids.Contains(r.ItemId)).GroupBy(r => r.ItemId), ct);
            var playlists = await db.PlaylistEntries.Where(e => ids.Contains(e.TrackId)).GroupBy(e => e.TrackId)
                .Select(x => new { x.Key, Count = x.Select(e => e.PlaylistId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var roots = LibraryScanner.RootsOf(options.Value);

            DuplicateCopyDto Copy(Track t) => new(t.Id, t.Title, t.ArtistName, t.AlbumTitle, AdminEndpoints.RelativePath(t.FilePath, roots), t.Format,
                t.BitrateKbps, t.SampleRate, t.IsLossless, t.DurationSeconds, t.FileSize, UploadArea.Contains(options.Value, t.FilePath),
                plays.GetValueOrDefault(t.Id), favorites.GetValueOrDefault(t.Id), playlists.GetValueOrDefault(t.Id), ratings.GetValueOrDefault(t.Id));

            var dtos = groups.Where(g => g.Ids.All(tracks.ContainsKey)).Select(g =>
            {
                // Meilleure qualité d'abord, puis la copie la plus écoutée, puis la plus ancienne.
                var copies = g.Ids.Select(id => Copy(tracks[id]))
                    .OrderByDescending(c => c.IsLossless).ThenByDescending(c => c.BitrateKbps ?? 0).ThenByDescending(c => c.SampleRate ?? 0)
                    .ThenByDescending(c => c.Plays).ThenBy(c => tracks[c.TrackId].CreatedAt).ToList();
                return new DuplicateGroupDto(g.Reason, g.RecordingId, copies[0].TrackId, copies);
            }).OrderBy(g => g.Copies[0].ArtistName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Copies[0].Title, StringComparer.CurrentCultureIgnoreCase).ToList();

            var total = await db.Tracks.CountAsync(ct);
            return new DuplicatesDto(total, total - await FingerprintJob.Pending(db).CountAsync(ct), fingerprinter.Available, dtos);
        });

        // Empreintes manquantes : une suite dans la file « acoustid » (pause, heures calmes), sans doublon tant qu'elle attend.
        g.MapPost("/fingerprints", async (HttpContext http, SonNuageDbContext db, JobEngine engine, IFingerprinter fingerprinter, CancellationToken ct) =>
        {
            if (!fingerprinter.Available) return Results.Problem(Tx.Key("errors.jobs.noFpcalc"), statusCode: 409);
            var suite = await engine.EnqueueOnceAsync(db, FingerprintJob.Plan(http.User.UserId()), ct);
            return Results.Ok(suite?.Id);
        });

        // Garde une copie, écarte les autres : action destructrice, confirmée dans l'interface et tracée dans le volet Tâches.
        g.MapPost("/resolve", async (ResolveDuplicatesRequest req, HttpContext http, SonNuageDbContext db, DuplicateResolver resolver, CancellationToken ct) =>
        {
            var removeIds = req.RemoveIds?.Distinct().ToList() ?? [];
            if (removeIds.Count is 0 or > DuplicateResolver.MaxCopies || removeIds.Contains(req.KeepId)) return Results.BadRequest();
            var all = removeIds.Append(req.KeepId).ToList();
            var tracks = await db.Tracks.AsNoTracking().Where(t => all.Contains(t.Id)).ToListAsync(ct);
            if (tracks.Count != all.Count) return Results.NotFound();
            if (!await DuplicateResolver.AreDuplicatesAsync(db, all, ct)) return Results.Problem(Tx.Key("errors.duplicates.notDuplicates"), statusCode: 409);
            var keep = tracks.Single(t => t.Id == req.KeepId);
            var removed = tracks.Where(t => t.Id != req.KeepId).ToList();
            return Results.Ok(await resolver.ResolveAsync(db, keep, removed, http.User.UserId(), await NameAsync(db, http, ct), ct));
        });
    }

    private static Task<Dictionary<Guid, int>> CountsAsync<T>(IQueryable<IGrouping<Guid, T>> groups, CancellationToken ct) =>
        groups.Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    /// <summary>Filtres de la liste des propositions (lus dans la requête).</summary>
    public sealed record ProposalFilter(string? Status, Guid? SuiteId, string? Artist, double? MinScore, double? MaxScore)
    {
        /// <summary>Requête filtrée, ou null si un filtre est invalide.</summary>
        public IQueryable<RescanProposal>? Apply(IQueryable<RescanProposal> query)
        {
            if (MinScore is < 0 or > 1 || MaxScore is < 0 or > 1) return null;
            switch (Status?.Trim().ToLowerInvariant())
            {
                case null or "" or "pending": query = query.Where(p => p.Status == RescanProposalStatus.Pending); break;
                case "all": break;
                case var s when Enum.TryParse<RescanProposalStatus>(s, ignoreCase: true, out var status) && Enum.IsDefined(status):
                    query = query.Where(p => p.Status == status);
                    break;
                default: return null;
            }
            if (SuiteId is { } suite) query = query.Where(p => p.SuiteId == suite);
            if (MinScore is { } min) query = query.Where(p => p.Score >= min);
            if (MaxScore is { } max) query = query.Where(p => p.Score <= max);
            if (!string.IsNullOrWhiteSpace(Artist))
            {
                var like = $"%{Artist.Trim()}%";
                query = query.Where(p => EF.Functions.Like(p.Track.ArtistName, like) || EF.Functions.Like(p.Track.AlbumArtistName, like)
                    || EF.Functions.Like(p.ArtistName, like));
            }
            return query;
        }
    }

    private static IResult? Invalid(DecideProposalsRequest req) =>
        req.Ids is not { Count: > 0 and <= RescanReview.MaxBatch } ? Results.BadRequest() : null;

    private static async Task<string?> NameAsync(SonNuageDbContext db, HttpContext http, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == http.User.UserId()).Select(u => u.Username).FirstOrDefaultAsync(ct);

    private static RescanProposalDto ToDto(RescanProposal p, TagWriter tags)
    {
        var t = p.Track;
        // Ce que l'acceptation changerait aujourd'hui (le morceau a pu changer depuis le rescan).
        var candidate = JsonSerializer.Deserialize<MetadataCandidateDto>(p.Candidate, JobContext.Json)!;
        var changes = LibraryMatchJob.Evaluate(t, candidate, apply: true, threshold: 0).Changes
            .Select(c => new ProposalChangeDto(c.Field, c.From, c.To)).ToList();
        return new RescanProposalDto(p.Id, p.TrackId, p.SuiteId, p.Suite?.Title, p.Status.ToString(), p.Score,
            t.Title, t.ArtistName, t.AlbumTitle, p.Title, p.ArtistName, p.AlbumTitle, p.RecordingId, p.ReleaseId, changes,
            t.MetadataLocked, tags.IsWritable(t.FilePath), p.CreatedAt, p.DecidedAt, p.DecidedBy);
    }
}
