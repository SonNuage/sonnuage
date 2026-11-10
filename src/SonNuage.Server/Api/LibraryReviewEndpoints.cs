using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Api;

/// <summary>Revue de la bibliothèque dans l'admin : propositions des rescans AcoustID.</summary>
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
    }

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
