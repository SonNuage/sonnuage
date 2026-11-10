using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Search;

namespace SonNuage.Server.Library;

/// <summary>
/// Garde une copie d'un morceau en double et écarte les autres. Favoris, notes, écoutes, playlists et file Subsonic
/// passent sur la copie gardée. Une copie écartée dans uploads/ est supprimée du disque ; une copie de music/ (lecture
/// seule) est seulement masquée (<see cref="HiddenFile"/>, que le scan ignore). Rien n'est jamais supprimé hors
/// d'uploads/ (<see cref="UploadArea"/>). Tout est tracé dans une suite du volet Tâches.
/// </summary>
public sealed class DuplicateResolver(IOptions<SonNuageOptions> options, SearchIndexer search, TimeProvider time, ILogger<DuplicateResolver> logger)
{
    /// <summary>Copies écartées par envoi, au plus.</summary>
    public const int MaxCopies = 20;

    private sealed record Moved(int Favorites, int Ratings, int Plays, int PlaylistEntries);

    /// <summary>Morceaux tels que la recherche de doublons les voit (tous, ou ceux de <paramref name="ids"/>).</summary>
    public static async Task<List<DuplicateCandidate>> CandidatesAsync(SonNuageDbContext db, IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        var tracks = ids is null ? db.Tracks : db.Tracks.Where(t => ids.Contains(t.Id));
        var rows = await tracks.Select(t => new
        {
            t.Id,
            t.MusicBrainzRecordingId,
            t.DurationSeconds,
            Data = db.Fingerprints.Where(f => f.TrackId == t.Id).Select(f => f.Data).FirstOrDefault(),
        }).ToListAsync(ct);
        return rows.Select(r => new DuplicateCandidate(r.Id, r.MusicBrainzRecordingId, r.DurationSeconds, r.Data is null ? null : Fingerprints.FromBytes(r.Data))).ToList();
    }

    /// <summary>Les copies forment-elles un seul groupe de doublons (même enregistrement, de proche en proche) ?</summary>
    public static async Task<bool> AreDuplicatesAsync(SonNuageDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        DuplicateFinder.Find(await CandidatesAsync(db, ids, ct)) is [{ } group] && group.Ids.Count == ids.Count;

    public async Task<ResolveDuplicatesResultDto> ResolveAsync(SonNuageDbContext db, Track keep, IReadOnlyList<Track> removed, Guid? userId, string? by,
        CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var log = new List<(string Level, string Message)> { ("info", Tx.Key("jobs.duplicates.kept", ("path", Tx.Literal(keep.FilePath)))) };
        var toDelete = new List<string>();
        var hidden = new List<string>();

        // Base d'abord, en une transaction ; les fichiers seulement une fois tout enregistré.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var copy in removed)
            {
                var moved = await MergeAsync(db, copy.Id, keep.Id, ct);
                log.Add(("info", Tx.Key("jobs.duplicates.moved", ("path", Tx.Literal(copy.FilePath)), ("favorites", moved.Favorites),
                    ("ratings", moved.Ratings), ("plays", moved.Plays), ("playlists", moved.PlaylistEntries))));
                if (UploadArea.Contains(o, copy.FilePath))
                {
                    toDelete.Add(copy.FilePath);
                }
                else
                {
                    if (await db.HiddenFiles.FindAsync([copy.FilePath], ct) is null)
                        db.HiddenFiles.Add(new HiddenFile { Path = copy.FilePath, KeptTrackId = keep.Id, HiddenAt = now, HiddenBy = by });
                    hidden.Add(copy.FilePath);
                    log.Add(("info", Tx.Key("jobs.duplicates.hidden", ("path", Tx.Literal(copy.FilePath)))));
                }
            }
            await db.SaveChangesAsync(ct);
            var ids = removed.Select(r => r.Id).ToList();
            await db.Tracks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct);
            await LibraryIndexer.PruneAsync(db, ct);
            await tx.CommitAsync(ct);
        }

        var deleted = new List<string>();
        foreach (var path in toDelete)
        {
            try
            {
                if (!UploadArea.TryDelete(o, path)) continue;
                deleted.Add(path);
                log.Add(("info", Tx.Key("jobs.duplicates.deleted", ("path", Tx.Literal(path)))));
                logger.LogWarning("Doublon supprimé : {Path} (gardé : {Kept})", path, keep.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Add(("warn", Tx.Key("jobs.duplicates.deleteFailed", ("path", Tx.Literal(path)), ("error", Tx.Literal(ex.Message)))));
                logger.LogWarning("Doublon non supprimé : {Path} : {Message}", path, ex.Message);
            }
        }

        var title = Tx.Key("jobs.suites.duplicates", ("title", Tx.Literal($"{keep.ArtistName} - {keep.Title}")));
        var suiteId = await AuditTrail.RecordAsync(db, JobSuiteKinds.Duplicates, JobKinds.DuplicatesResolve, title, userId, log, now, ct);
        search.RequestReindex();
        var roots = LibraryScanner.RootsOf(o);
        return new ResolveDuplicatesResultDto(removed.Count, deleted.Select(p => AdminEndpoints.RelativePath(p, roots)).ToList(),
            hidden.Select(p => AdminEndpoints.RelativePath(p, roots)).ToList(), suiteId);
    }

    /// <summary>Passe sur <paramref name="to"/> tout ce que les comptes ont attaché à <paramref name="from"/>.</summary>
    private static async Task<Moved> MergeAsync(SonNuageDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        // Favoris : ceux qui n'avaient que la copie écartée l'ont désormais sur la copie gardée.
        var keptFavorites = await db.Favorites.Where(f => f.TrackId == to).Select(f => f.UserId).ToListAsync(ct);
        var newFavorites = await db.Favorites.Where(f => f.TrackId == from && !keptFavorites.Contains(f.UserId)).ToListAsync(ct);
        db.Favorites.AddRange(newFavorites.Select(f => new Favorite { UserId = f.UserId, TrackId = to, CreatedAt = f.CreatedAt }));

        // Notes (sans clé étrangère) : déplacées ; deux copies notées : la note la plus récente reste.
        var ratings = await db.Ratings.Where(r => r.ItemId == from).ToListAsync(ct);
        var keptRatings = await db.Ratings.Where(r => r.ItemId == to).ToDictionaryAsync(r => r.UserId, ct);
        foreach (var r in ratings)
        {
            if (!keptRatings.TryGetValue(r.UserId, out var kept))
                db.Ratings.Add(new UserRating { UserId = r.UserId, ItemId = to, Value = r.Value, UpdatedAt = r.UpdatedAt });
            else if (r.UpdatedAt > kept.UpdatedAt)
                (kept.Value, kept.UpdatedAt) = (r.Value, r.UpdatedAt);
            db.Ratings.Remove(r);
        }
        await db.SaveChangesAsync(ct);

        var plays = await db.PlayEvents.Where(p => p.TrackId == from).ExecuteUpdateAsync(s => s.SetProperty(p => p.TrackId, to), ct);
        // Les playlists gardent leur ordre : l'entrée pointe simplement sur l'autre copie.
        var entries = await db.PlaylistEntries.Where(e => e.TrackId == from).ExecuteUpdateAsync(s => s.SetProperty(e => e.TrackId, to), ct);
        await db.SavedQueueEntries.Where(e => e.TrackId == from).ExecuteUpdateAsync(s => s.SetProperty(e => e.TrackId, to), ct);
        await db.SavedQueues.Where(q => q.CurrentTrackId == from).ExecuteUpdateAsync(s => s.SetProperty(q => q.CurrentTrackId, (Guid?)to), ct);
        await db.YoutubeItems.Where(y => y.TrackId == from).ExecuteUpdateAsync(s => s.SetProperty(y => y.TrackId, (Guid?)to), ct);
        return new Moved(newFavorites.Count, ratings.Count, plays, entries);
    }
}
