using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Règles de l'analyse audio. Une analyse (réussie ou en échec) est à jour tant que le fichier n'a pas changé et que
/// la version du calcul n'a pas monté : un échec n'est pas retenté avant l'un ou l'autre.
/// </summary>
public static class AudioAnalysis
{
    /// <summary>Analyses à jour.</summary>
    public static IQueryable<TrackAudioFeatures> UpToDate(SonNuageDbContext db) =>
        db.AudioFeatures.Where(f => f.Version >= TrackAudioFeatures.CurrentVersion && f.FileModifiedUtc == f.Track.FileModifiedUtc);

    /// <summary>Morceaux à analyser : jamais analysés, analysés par une version dépassée, ou dont le fichier a changé.</summary>
    public static IQueryable<Track> Pending(SonNuageDbContext db)
    {
        var upToDate = UpToDate(db);
        return db.Tracks.Where(t => !upToDate.Any(f => f.TrackId == t.Id));
    }

    /// <summary>Tâches d'analyse en cours ou en attente.</summary>
    public static IQueryable<Job> ActiveJobs(SonNuageDbContext db) =>
        db.Jobs.Where(j => j.Kind == JobKinds.AudioAnalysis.Name
                           && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued || j.Status == JobStatus.Running));

    /// <summary>Inscrit le résultat de l'analyse du fichier daté <paramref name="fileModifiedUtc"/>.</summary>
    public static void Record(TrackAudioFeatures row, AudioAnalysisOutcome outcome, DateTime fileModifiedUtc, DateTimeOffset now)
    {
        row.Version = TrackAudioFeatures.CurrentVersion;
        row.AnalyzedAt = now;
        row.FileModifiedUtc = fileModifiedUtc;
        var values = outcome is AudioAnalysisOutcome.Analyzed a ? a.Values : null;
        row.Error = outcome is AudioAnalysisOutcome.Failed f ? Truncate(f.Error, TrackAudioFeatures.MaxErrorLength) : null;
        row.Bpm = values?.Bpm;
        row.Danceability = values?.Danceability;
        row.Energy = values?.Energy;
        row.Valence = values?.Valence;
        row.Happy = values?.Happy;
        row.Sad = values?.Sad;
        row.Relaxed = values?.Relaxed;
        row.Aggressive = values?.Aggressive;
        row.Party = values?.Party;
        row.Acoustic = values?.Acoustic;
        row.Electronic = values?.Electronic;
        row.Instrumental = values?.Instrumental;
    }

    public static async Task<AudioAnalysisStatusDto> StatusAsync(SonNuageDbContext db, bool available, bool enabled, CancellationToken ct)
    {
        var total = await db.Tracks.CountAsync(ct);
        var pending = await Pending(db).CountAsync(ct);
        var errors = await UpToDate(db).CountAsync(f => f.Error != null, ct);
        var running = await ActiveJobs(db).AnyAsync(ct);
        return new AudioAnalysisStatusDto(available, available && enabled, total, total - pending - errors, errors, pending, running);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
