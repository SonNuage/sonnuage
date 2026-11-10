using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Library;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Empreintes acoustiques d'un lot de morceaux qui n'en ont pas (ou dont le fichier a changé), les plus récents
/// d'abord. Chaque résultat est enregistré aussitôt : une tâche interrompue (pause, heures calmes) reprend où elle en
/// était. S'il en reste, la tâche ajoute le lot suivant à sa suite. Sans fpcalc, la tâche s'arrête sans erreur.
/// </summary>
public sealed class FingerprintJob(IFingerprinter fingerprinter, TimeProvider time) : IJobHandler
{
    /// <summary>Morceaux par tâche (une fraction de seconde chacun) : les imports passent entre deux lots.</summary>
    internal static int BatchSize = 100;

    public JobKind Kind => JobKinds.Fingerprint;

    public static JobPlan Plan(Guid? createdById = null) =>
        Next(new JobPlan(JobSuiteKinds.Fingerprints, Tx.Key("jobs.suites.fingerprints"), createdById));

    private static JobPlan Next(JobPlan plan) => plan.Then(JobKinds.Fingerprint, Tx.Key("jobs.steps.fingerprints"));

    /// <summary>Morceaux sans empreinte à jour (jamais calculée, ou fichier modifié depuis).</summary>
    public static IQueryable<Track> Pending(SonNuageDbContext db) =>
        db.Tracks.Where(t => !db.Fingerprints.Any(f => f.TrackId == t.Id && f.FileModifiedUtc == t.FileModifiedUtc));

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        if (!fingerprinter.Available) throw new JobAbortedException(Tx.Key("errors.jobs.noFpcalc"));
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var batch = await Pending(db).OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.FilePath, t.FileModifiedUtc, t.ArtistName, t.Title })
            .Take(BatchSize).ToListAsync(ct);

        var failed = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var t = batch[i];
            RawFingerprint? print = null;
            string? error = null;
            try
            {
                print = await fingerprinter.ComputeAsync(t.FilePath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                failed++;
                error = ex.Message;
                ctx.Warn(Tx.Key("jobs.fingerprints.failed", ("track", Tx.Literal($"{t.ArtistName} - {t.Title}")), ("error", Tx.Literal(ex.Message))));
            }
            await SaveAsync(db, t.Id, t.FileModifiedUtc, print, error, ct);
            ctx.Report((i + 1.0) / batch.Count);
        }
        ctx.Info(Tx.Key("jobs.fingerprints.done", ("computed", batch.Count - failed), ("failed", failed)));

        // Lot suivant, sans reprendre ceux de ce lot : pas de boucle sans fin si un résultat n'a pas pu être enregistré.
        var done = batch.Select(t => t.Id).ToList();
        if (done.Count > 0 && await Pending(db).AnyAsync(t => !done.Contains(t.Id), ct))
            await ctx.AppendAsync(db, Next(new JobPlan(JobSuiteKinds.Fingerprints, "")));
    }

    private async Task SaveAsync(SonNuageDbContext db, Guid trackId, DateTime fileModifiedUtc, RawFingerprint? print, string? error, CancellationToken ct)
    {
        try
        {
            var row = await db.Fingerprints.FirstOrDefaultAsync(f => f.TrackId == trackId, ct);
            if (row is null)
            {
                // Morceau supprimé pendant le calcul : rien à garder.
                if (!await db.Tracks.AnyAsync(t => t.Id == trackId, ct)) return;
                row = new TrackFingerprint { TrackId = trackId };
                db.Fingerprints.Add(row);
            }
            row.Data = print is null ? null : Fingerprints.ToBytes(print.Values.Take(Fingerprints.MaxValues).ToArray());
            row.DurationSeconds = print?.DurationSeconds ?? 0;
            row.FileModifiedUtc = fileModifiedUtc;
            row.ComputedAt = time.GetUtcNow();
            row.Error = error is null || error.Length <= TrackFingerprint.MaxErrorLength ? error : error[..TrackFingerprint.MaxErrorLength];
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Supprimé entre-temps (clé étrangère) : idem.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
