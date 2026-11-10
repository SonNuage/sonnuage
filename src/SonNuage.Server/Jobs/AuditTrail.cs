using SonNuage.Server.Data;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Trace d'une action faite sur le moment depuis l'admin (doublons écartés, fichiers supprimés) : une suite terminée
/// avec son journal, visible dans l'historique du volet Tâches comme les traitements du moteur.
/// </summary>
public static class AuditTrail
{
    public static async Task<Guid> RecordAsync(SonNuageDbContext db, string suiteKind, JobKind kind, string title, Guid? userId,
        IReadOnlyList<(string Level, string Message)> lines, DateTimeOffset now, CancellationToken ct)
    {
        var plan = new JobPlan(suiteKind, title, userId).Then(kind, title);
        var job = plan.Last;
        job.Status = JobStatus.Succeeded;
        job.Attempts = 1;
        job.Progress = 1;
        job.NotBefore = now;
        job.StartedAt = now;
        job.FinishedAt = now;
        plan.Suite.CreatedAt = now;
        job.CreatedAt = now;
        db.JobSuites.Add(plan.Suite);
        db.Jobs.Add(job);
        db.JobLogs.AddRange(lines.Select(l => new JobLog { JobId = job.Id, At = now, Level = l.Level, Message = l.Message }));
        await db.SaveChangesAsync(ct);
        return plan.Suite.Id;
    }
}
