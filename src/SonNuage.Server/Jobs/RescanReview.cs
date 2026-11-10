using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Search;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Issue d'une comparaison MusicBrainz et revue groupée de ce qu'elle propose. Une seule voie d'application
/// (<see cref="ApplyAsync"/>) pour la correction sûre d'un rescan et pour la proposition acceptée par l'admin : tags
/// réécrits dans uploads/, rangement dans le bon album, pochettes, réindexation de la recherche. Les morceaux modifiés à
/// la main ne sont jamais touchés. Un refus est retenu par morceau et par résultat (<see cref="Signature"/>).
/// </summary>
public sealed class RescanReview(CoverLocator covers, TagWriter tags, SearchIndexer search, TimeProvider time)
{
    /// <summary>Propositions tranchées par envoi, au plus.</summary>
    public const int MaxBatch = 500;

    /// <summary>Empreinte du résultat proposé : même titre, artiste, album et identifiants = même proposition, quel que soit le score.</summary>
    public static string Signature(MetadataCandidateDto c)
    {
        static string Text(string? s) => s?.Trim().ToLowerInvariant() ?? "";
        static string Id(string? s) => MusicBrainzId.Parse(s)?.ToString() ?? "";
        var key = string.Join('\n', Text(c.Title), Text(c.Artist), Text(c.Album), Id(c.RecordingId), Id(c.ReleaseId));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// Fin de la comparaison d'un morceau : applique la correction sûre, garde la proposition pour la revue (rescan
    /// sans « appliquer », score sous le seuil), ou s'abstient si l'admin a déjà refusé ce résultat.
    /// </summary>
    internal async Task<LibraryMatchJob.MatchOutcome> ConcludeAsync(SonNuageDbContext db, Track track, MetadataCandidateDto best, bool apply, double threshold,
        Guid? suiteId, CancellationToken ct)
    {
        var signature = Signature(best);
        var rejected = await db.RescanProposals.AnyAsync(p => p.TrackId == track.Id && p.Signature == signature && p.Status == RescanProposalStatus.Rejected, ct);
        var outcome = LibraryMatchJob.Evaluate(track, best, apply, threshold, rejected);
        if (outcome.Result == "applied") await ApplyAsync(db, [(track, outcome)], ct);
        await RecordAsync(db, track.Id, best, signature, outcome.Result, suiteId, ct);
        return outcome;
    }

    /// <summary>
    /// Une proposition en attente par morceau : le même résultat met à jour la sienne, un autre la remplace ; une
    /// correction appliquée ou des tags déjà conformes la rendent caduque. Refus et morceau verrouillé : rien ne change.
    /// </summary>
    private async Task RecordAsync(SonNuageDbContext db, Guid trackId, MetadataCandidateDto best, string signature, string result, Guid? suiteId, CancellationToken ct)
    {
        if (result is "rejected" or "locked") return;
        var now = time.GetUtcNow();
        var keep = result is "proposed" or "low";
        var pending = await db.RescanProposals.Where(p => p.TrackId == trackId && p.Status == RescanProposalStatus.Pending).ToListAsync(ct);
        foreach (var old in pending.Where(p => !keep || p.Signature != signature))
        {
            old.Status = RescanProposalStatus.Superseded;
            old.DecidedAt = now;
        }
        if (keep)
        {
            var proposal = pending.FirstOrDefault(p => p.Signature == signature);
            if (proposal is null)
            {
                proposal = new RescanProposal
                {
                    TrackId = trackId,
                    Signature = signature,
                    Candidate = "",
                    Title = Cut(best.Title)!,
                    ArtistName = Cut(best.Artist)!,
                    AlbumTitle = Cut(best.Album),
                    RecordingId = MusicBrainzId.Parse(best.RecordingId),
                    ReleaseId = MusicBrainzId.Parse(best.ReleaseId),
                };
                db.RescanProposals.Add(proposal);
            }
            proposal.Candidate = JsonSerializer.Serialize(best, JobContext.Json);
            proposal.Score = best.Score;
            proposal.SuiteId = suiteId;
            proposal.CreatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Accepte des propositions en attente : chacune est rejouée sur le morceau tel qu'il est aujourd'hui.</summary>
    public async Task<DecideProposalsResultDto> AcceptAsync(SonNuageDbContext db, IReadOnlyCollection<Guid> ids, string? by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var proposals = await db.RescanProposals.Include(p => p.Track)
            .Where(p => ids.Contains(p.Id) && p.Status == RescanProposalStatus.Pending).ToListAsync(ct);
        var changes = new List<(Track, LibraryMatchJob.MatchOutcome)>();
        foreach (var p in proposals)
        {
            var candidate = JsonSerializer.Deserialize<MetadataCandidateDto>(p.Candidate, JobContext.Json)!;
            // Seuil nul : l'admin a tranché. Le verrou d'une modification à la main, lui, reste respecté.
            var outcome = LibraryMatchJob.Evaluate(p.Track, candidate, apply: true, threshold: 0);
            var applies = outcome.Result == "applied" && changes.TrueForAll(c => c.Item1.Id != p.TrackId);
            if (applies) changes.Add((p.Track, outcome));
            p.Status = applies ? RescanProposalStatus.Accepted : RescanProposalStatus.Superseded;
            p.DecidedAt = now;
            p.DecidedBy = Cut(by, RescanProposal.MaxDecidedByLength);
        }
        await ApplyAsync(db, changes, ct);
        await db.SaveChangesAsync(ct);
        return new DecideProposalsResultDto(changes.Count, proposals.Count - changes.Count);
    }

    /// <summary>Refuse des propositions en attente : le même résultat ne sera plus proposé pour ces morceaux.</summary>
    public async Task<DecideProposalsResultDto> RejectAsync(SonNuageDbContext db, IReadOnlyCollection<Guid> ids, string? by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var decidedBy = Cut(by, RescanProposal.MaxDecidedByLength);
        var count = await db.RescanProposals.Where(p => ids.Contains(p.Id) && p.Status == RescanProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, RescanProposalStatus.Rejected)
                .SetProperty(p => p.DecidedAt, now)
                .SetProperty(p => p.DecidedBy, decidedBy), ct);
        return new DecideProposalsResultDto(count, 0);
    }

    /// <summary>Applique des corrections : base, tags des fichiers d'uploads/, albums, pochettes, recherche.</summary>
    private async Task ApplyAsync(SonNuageDbContext db, IReadOnlyList<(Track Track, LibraryMatchJob.MatchOutcome Outcome)> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return;
        foreach (var (track, outcome) in changes) outcome.Apply(track);
        await AdminEndpoints.ReassignAsync(db, covers, tags, changes.Select(c => c.Track).ToList(), ct);
        search.RequestReindex();
    }

    /// <summary>Champs proposés tronqués à la taille de leur colonne.</summary>
    private static string? Cut(string? s, int max = RescanProposal.MaxTextLength) => s is null || s.Length <= max ? s : s[..max];
}
