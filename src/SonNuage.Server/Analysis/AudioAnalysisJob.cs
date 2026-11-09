using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Analyse audio d'un lot de morceaux en attente (<see cref="AudioAnalysis.Pending"/>), les plus récents d'abord.
/// Chaque résultat est enregistré aussitôt : une tâche interrompue reprend où elle en était. S'il reste des morceaux,
/// la tâche ajoute le lot suivant à sa suite. Analyse désactivée ou analyseur absent : la tâche s'arrête sans erreur.
/// </summary>
public sealed class AudioAnalysisJob(IAudioAnalyzer analyzer, AudioAnalysisSettingsStore settings, TimeProvider time) : IJobHandler
{
    /// <summary>Morceaux par tâche (quelques secondes chacun).</summary>
    internal static int BatchSize = 50;

    public JobKind Kind => JobKinds.AudioAnalysis;

    public static JobPlan Plan() => Next(new JobPlan(JobSuiteKinds.AudioAnalysis, Tx.Key("jobs.suites.analysis")));

    private static JobPlan Next(JobPlan plan) => plan.Then(JobKinds.AudioAnalysis, Tx.Key("jobs.steps.analysis"));

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        EnsureEnabled();
        var batch = await AudioAnalysis.Pending(db).OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.FilePath, t.FileModifiedUtc, t.ArtistName, t.Title })
            .Take(BatchSize).ToListAsync(ct);

        var failed = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            EnsureEnabled();
            var t = batch[i];
            var outcome = await analyzer.AnalyzeAsync(t.FilePath, ct);
            if (outcome is AudioAnalysisOutcome.Failed f)
            {
                failed++;
                ctx.Warn(Tx.Key("jobs.analysis.failed", ("track", Tx.Literal($"{t.ArtistName} - {t.Title}")), ("error", Tx.Literal(f.Error))));
            }
            await SaveAsync(db, t.Id, t.FileModifiedUtc, outcome, ct);
            ctx.Report((i + 1.0) / batch.Count);
        }
        ctx.Info(Tx.Key("jobs.analysis.done", ("analyzed", batch.Count - failed), ("failed", failed)));

        // Lot suivant, sans reprendre ceux de ce lot (un fichier modifié pendant son analyse attendra le prochain scan) :
        // pas de boucle sans fin si un résultat ne pouvait pas être enregistré.
        var done = batch.Select(t => t.Id).ToList();
        if (done.Count > 0 && await AudioAnalysis.Pending(db).AnyAsync(t => !done.Contains(t.Id), ct))
            await ctx.AppendAsync(db, Next(new JobPlan(JobSuiteKinds.AudioAnalysis, "")));
    }

    private void EnsureEnabled()
    {
        if (!analyzer.Available) throw new JobAbortedException(Tx.Key("jobs.analysis.unavailable"));
        if (!settings.Settings.Enabled) throw new JobAbortedException(Tx.Key("jobs.analysis.disabled"));
    }

    private async Task SaveAsync(SonNuageDbContext db, Guid trackId, DateTime fileModifiedUtc, AudioAnalysisOutcome outcome, CancellationToken ct)
    {
        try
        {
            var row = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackId == trackId, ct);
            if (row is null)
            {
                // Morceau supprimé pendant son analyse : rien à garder.
                if (!await db.Tracks.AnyAsync(t => t.Id == trackId, ct)) return;
                row = new TrackAudioFeatures { TrackId = trackId };
                db.AudioFeatures.Add(row);
            }
            AudioAnalysis.Record(row, outcome, fileModifiedUtc, time.GetUtcNow());
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
