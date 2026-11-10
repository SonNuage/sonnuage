using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Analysis;
using SonNuage.Server.Data;

namespace SonNuage.Server.Jobs;

/// <summary>Tâches planifiées : vérification des demandes, passages, et lancement de la tâche correspondante.</summary>
public static class JobSchedules
{
    /// <summary>Erreur (clé de traduction) si la demande est invalide, sinon null.</summary>
    public static string? Validate(JobScheduleRequest r)
    {
        if (!ScheduledJobKinds.All.Contains(r.Kind)) return Tx.Key("errors.jobs.unknownKind");
        if (r.Days is not { Count: > 0 } || r.Days.Any(d => d is < 0 or > 6)) return Tx.Key("errors.jobs.scheduleDays");
        if (QuietHours.Zone(r.TimeZone) is null) return Tx.Key("errors.jobs.scheduleZone");
        return null;
    }

    /// <summary>Recopie une demande déjà vérifiée. Les passages d'avant la modification ne comptent plus.</summary>
    public static void Apply(JobSchedule s, JobScheduleRequest r, DateTimeOffset now)
    {
        s.Kind = r.Kind;
        s.Days = r.Days.Aggregate(0, (mask, d) => mask | 1 << d);
        s.At = new TimeOnly(r.At.Hour, r.At.Minute);
        s.TimeZone = r.TimeZone;
        s.Enabled = r.Enabled;
        // Seul le rescan AcoustID a des options ; seuil borné comme à la création d'une tâche.
        var o = r.Kind == ScheduledJobKinds.AcoustIdRescan ? r.Options ?? new JobScheduleOptions() : null;
        s.Options = o is null ? null : JsonSerializer.Serialize(o with { Threshold = o.Threshold is { } t ? Math.Clamp(t, 0.5, 1) : null }, JobContext.Json);
        s.UpdatedAt = now;
    }

    public static ScheduleTimes Times(JobSchedule s) =>
        new(Enum.GetValues<DayOfWeek>().Where(d => (s.Days & 1 << (int)d) != 0).ToList(), s.At, QuietHours.Zone(s.TimeZone) ?? TimeZoneInfo.Utc);

    /// <summary>Un passage a eu lieu depuis le dernier lancement (ou la dernière modification).</summary>
    public static bool IsDue(JobSchedule s, DateTimeOffset now) =>
        s.Enabled && JobScheduling.IsDue(Times(s), s.LastRunAt is { } last && last > s.UpdatedAt ? last : s.UpdatedAt, now);

    public static JobScheduleDto ToDto(JobSchedule s, DateTimeOffset now) => new(
        s.Id, s.Kind, Enumerable.Range(0, 7).Where(d => (s.Days & 1 << d) != 0).ToList(), s.At, s.TimeZone, s.Enabled,
        s.Options is null ? null : JsonSerializer.Deserialize<JobScheduleOptions>(s.Options, JobContext.Json),
        s.CreatedAt, s.LastRunAt, s.LastSuiteId, s.Enabled ? JobScheduling.NextSlot(Times(s), now) : null);

    /// <summary>
    /// Demande la tâche (sans doublon si la même attend déjà son tour) et retient le lancement. Renvoie la suite créée,
    /// ou null (tâche identique déjà en attente, analyse audio désactivée ou sans morceau à analyser).
    /// </summary>
    public static async Task<Guid?> LaunchAsync(IServiceProvider services, SonNuageDbContext db, JobSchedule s, DateTimeOffset now, CancellationToken ct)
    {
        var suite = (await RequestAsync(services, db, s, ct))?.Id;
        await db.JobSchedules.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u =>
        {
            u.SetProperty(x => x.LastRunAt, now);
            if (suite is { } id) u.SetProperty(x => x.LastSuiteId, id);
        }, ct);
        s.LastRunAt = now;
        s.LastSuiteId = suite ?? s.LastSuiteId;
        return suite;
    }

    private static async Task<JobSuite?> RequestAsync(IServiceProvider services, SonNuageDbContext db, JobSchedule s, CancellationToken ct)
    {
        if (s.Kind == ScheduledJobKinds.AudioAnalysis) return await services.GetRequiredService<AudioAnalysisScheduler>().RunNowAsync(ct);
        var engine = services.GetRequiredService<JobEngine>();
        return await engine.EnqueueOnceAsync(db, Plan(s), ct);
    }

    private static JobPlan Plan(JobSchedule s)
    {
        switch (s.Kind)
        {
            case ScheduledJobKinds.AcoustIdRescan:
                var o = (s.Options is null ? null : JsonSerializer.Deserialize<JobScheduleOptions>(s.Options, JobContext.Json)) ?? new JobScheduleOptions();
                return AcoustIdRescanJob.Plan(Tx.Key("jobs.suites.rescanAll"), null, o.Apply, o.Apply ? o.Threshold : null, null, o.IncludeIdentified);
            case ScheduledJobKinds.LibraryScan:
                return MaintenanceJobs.Scan();
            case ScheduledJobKinds.SearchReindex:
                return MaintenanceJobs.Reindex();
            case ScheduledJobKinds.TranscodePurge:
                return MaintenanceJobs.PurgeTranscodeCache();
            case ScheduledJobKinds.DiscoveryRefresh:
                return MaintenanceJobs.DiscoveryRefresh();
            case ScheduledJobKinds.DatabaseBackup:
                return MaintenanceJobs.DatabaseBackup();
            default:
                throw new InvalidOperationException(Tx.Key("errors.jobs.unknownKind"));
        }
    }
}

/// <summary>
/// Lance les tâches planifiées dont un passage est atteint (vérifié chaque minute) : un serveur éteint à l'heure prévue
/// lance la tâche une fois à son retour. Chaque lancement est réservé en base : deux instances ne lancent pas la même.
/// </summary>
public sealed class JobScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<JobScheduler> logger) : BackgroundService
{
    internal static TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    internal static TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Tâches planifiées non lancées");
                }
                await Task.Delay(CheckEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Lance chaque tâche planifiée due. Renvoie le nombre de lancements.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var now = time.GetUtcNow();
        var launched = 0;
        foreach (var s in await db.JobSchedules.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct))
        {
            if (!JobSchedules.IsDue(s, now)) continue;
            // Réservation : seule l'instance qui retient ce lancement la première le fait.
            var claimed = await db.JobSchedules.Where(x => x.Id == s.Id && x.LastRunAt == s.LastRunAt && x.UpdatedAt == s.UpdatedAt)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastRunAt, now), ct);
            if (claimed == 0) continue;
            try
            {
                var suite = await JobSchedules.LaunchAsync(scope.ServiceProvider, db, s, now, ct);
                logger.LogInformation("Tâche planifiée {Kind} lancée (suite {Suite})", s.Kind, suite);
                launched++;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or JsonException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Tâche planifiée {Kind} : lancement impossible", s.Kind);
            }
        }
        return launched;
    }
}
