using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Youtube;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Gestionnaire de tâches. La file vit en base (table Jobs). Chaque file d'exécution (<see cref="JobLanes"/>) a son
/// consommateur, qui réserve les tâches une à une par une requête atomique vérifiant la capacité de la file
/// pour toutes les instances à la fois. Une tâche réservée porte un bail renouvelé tant qu'elle tourne : si son
/// instance disparaît, elle revient dans la file à l'expiration du bail. À la réussite, les tâches qui en
/// dépendent passent dans la file. À l'échec ou à l'annulation, elles sont annulées.
/// </summary>
public sealed class JobEngine(
    IServiceScopeFactory scopes,
    IEnumerable<IJobHandler> handlers,
    YoutubeSettingsStore youtubeSettings,
    IOptions<SonNuageOptions> options,
    TimeProvider time,
    ILogger<JobEngine> logger) : BackgroundService
{
    internal static TimeSpan Lease = TimeSpan.FromSeconds(60);
    internal static TimeSpan Heartbeat = TimeSpan.FromSeconds(2);
    internal static TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    /// <summary>Attente avant une nouvelle tentative, multipliée par le numéro de la tentative échouée.</summary>
    internal static TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaxCrashRestarts = 5;
    private static readonly DateTimeOffsetToBinaryConverter Dates = new();

    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Kind.Name);
    private readonly Dictionary<string, Channel<bool>> _signals = JobLanes.All.ToDictionary(l => l,
        _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }));
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly Channel<JobLog> _logs = Channel.CreateUnbounded<JobLog>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _workerId = $"{options.Value.Jobs.WorkerName}:{Guid.NewGuid():N}";

    /// <summary>Tâches simultanées permises dans une file, toutes instances confondues.</summary>
    public int Capacity(string lane) => lane switch
    {
        JobLanes.Download => Math.Clamp(youtubeSettings.Settings.ParallelDownloads, 1, YoutubeSettingsStore.MaxParallelDownloads),
        _ => 1,
    };

    public void Wake(string lane)
    {
        if (_signals.TryGetValue(lane, out var signal)) signal.Writer.TryWrite(true);
    }

    public void WakeAll()
    {
        foreach (var lane in JobLanes.All) Wake(lane);
    }

    /// <summary>Enregistre la suite et réveille les files concernées.</summary>
    public async Task<JobSuite> EnqueueAsync(SonNuageDbContext db, JobPlan plan, CancellationToken ct)
    {
        await EnqueueAsync(db, [plan], ct);
        return plan.Suite;
    }

    public async Task EnqueueAsync(SonNuageDbContext db, IReadOnlyList<JobPlan> plans, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var order = 0;
        foreach (var plan in plans)
        {
            plan.Suite.CreatedAt = now.AddTicks(order);
            db.JobSuites.Add(plan.Suite);
            // Dates croissantes : l'ordre d'ajout est l'ordre de passage à priorité égale.
            foreach (var job in plan.Jobs)
            {
                job.CreatedAt = now.AddTicks(order++);
                job.NotBefore = now;
            }
            db.Jobs.AddRange(plan.Jobs);
        }
        await db.SaveChangesAsync(ct);
        foreach (var lane in plans.SelectMany(p => p.Jobs).Where(j => j.Status == JobStatus.Queued).Select(j => j.Lane).Distinct()) Wake(lane);
    }

    /// <summary>
    /// Enregistre une suite d'une seule tâche, sauf si une tâche du même type et du même objet attend déjà son tour :
    /// une demande répétée (scan, réindexation, synchronisation) n'empile pas des doublons. Renvoie la suite créée, ou null.
    /// </summary>
    public async Task<JobSuite?> EnqueueOnceAsync(SonNuageDbContext db, JobPlan plan, CancellationToken ct)
    {
        var job = plan.Jobs.Single();
        var waiting = await db.Jobs.AnyAsync(j => j.Kind == job.Kind && j.SubjectId == job.SubjectId
                                                  && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued), ct);
        return waiting ? null : await EnqueueAsync(db, plan, ct);
    }

    /// <summary>Ajoute à une suite existante les tâches d'un plan (une tâche qui en prépare d'autres).</summary>
    public async Task AppendAsync(SonNuageDbContext db, Guid suiteId, JobPlan plan, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var order = 0;
        foreach (var job in plan.Jobs)
        {
            job.SuiteId = suiteId;
            job.CreatedAt = now.AddTicks(order++);
            job.NotBefore = now;
        }
        db.Jobs.AddRange(plan.Jobs);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        foreach (var lane in plan.Jobs.Where(j => j.Status == JobStatus.Queued).Select(j => j.Lane).Distinct()) Wake(lane);
    }

    private async Task NotifyCanceledAsync(IReadOnlyCollection<(string Kind, Guid? SubjectId)> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0) return;
        using var scope = scopes.CreateScope();
        foreach (var (kind, subject) in jobs.Distinct())
        {
            if (!_handlers.TryGetValue(kind, out var handler)) continue;
            try
            {
                await handler.CanceledAsync(scope.ServiceProvider, subject, ct);
            }
            catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Annulation de {Kind} : remise en ordre impossible", kind);
            }
        }
    }

    /// <summary>Ligne de journal d'une tâche (écrite en base par lots, lisible en direct depuis l'admin).</summary>
    public void Log(Guid jobId, string level, string message) =>
        _logs.Writer.TryWrite(new JobLog { JobId = jobId, At = time.GetUtcNow(), Level = level, Message = message.Length <= 4000 ? message : message[..4000] });

    /// <summary>Annule une tâche : tout de suite si elle attend, à son prochain point d'arrêt si elle tourne.</summary>
    public async Task<bool> CancelAsync(SonNuageDbContext db, Guid jobId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var info = await db.Jobs.Where(j => j.Id == jobId).Select(j => new { j.Kind, j.SubjectId }).FirstOrDefaultAsync(ct);
        if (info is null) return false;
        var waiting = await db.Jobs.Where(j => j.Id == jobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Canceled).SetProperty(j => j.FinishedAt, now), ct);
        if (waiting > 0)
        {
            Log(jobId, "info", Tx.Key("jobs.log.canceled"));
            await CancelDependentsAsync(db, jobId, ct);
            await NotifyCanceledAsync([(info.Kind, info.SubjectId)], ct);
            return true;
        }
        var running = await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true), ct);
        if (_running.TryGetValue(jobId, out var cts)) await cts.CancelAsync();
        return running > 0;
    }

    public async Task CancelSuiteAsync(SonNuageDbContext db, Guid suiteId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var waiting = db.Jobs.Where(j => j.SuiteId == suiteId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued));
        var subjects = (await waiting.Select(j => new { j.Kind, j.SubjectId }).Distinct().ToListAsync(ct)).Select(j => (j.Kind, j.SubjectId)).ToList();
        await waiting.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Canceled).SetProperty(j => j.FinishedAt, now), ct);
        await NotifyCanceledAsync(subjects, ct);
        foreach (var id in await db.Jobs.Where(j => j.SuiteId == suiteId && j.Status == JobStatus.Running).Select(j => j.Id).ToListAsync(ct))
            await CancelAsync(db, id, ct);
    }

    /// <summary>Relance une tâche en échec ou annulée, et les tâches de la suite annulées à cause d'elle.</summary>
    public async Task<bool> RetryAsync(SonNuageDbContext db, Guid jobId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is not (JobStatus.Failed or JobStatus.Canceled)) return false;
        var dependsOnDone = job.DependsOnId is not { } dep || await db.Jobs.AnyAsync(j => j.Id == dep && j.Status == JobStatus.Succeeded, ct);
        if (!dependsOnDone) return false;

        // Les tâches suivantes redeviennent « en attente » AVANT que la tâche reparte : sinon elle pourrait finir entre
        // les deux, sans personne à débloquer, et la suite resterait en attente pour toujours.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        for (var ids = new List<Guid> { jobId }; ids.Count > 0;)
        {
            var next = await db.Jobs.Where(j => j.DependsOnId != null && ids.Contains(j.DependsOnId.Value) && j.Status == JobStatus.Canceled)
                .Select(j => j.Id).ToListAsync(ct);
            await db.Jobs.Where(j => next.Contains(j.Id)).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.Error, (string?)null).SetProperty(j => j.FinishedAt, (DateTimeOffset?)null).SetProperty(j => j.Attempts, 0), ct);
            ids = next;
        }
        job.Status = JobStatus.Queued;
        job.Error = null;
        job.Attempts = 0;
        job.CancelRequested = false;
        job.NotBefore = now;
        job.FinishedAt = null;
        job.Progress = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Log(jobId, "info", Tx.Key("jobs.log.retried"));
        Wake(job.Lane);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flush = FlushLogsAsync(stoppingToken);
        if (!options.Value.Jobs.Worker)
        {
            await flush;
            return;
        }
        await RecoverOwnJobsAsync(stoppingToken);
        await Task.WhenAll(JobLanes.All.Select(l => LaneLoopAsync(l, stoppingToken)).Append(MaintenanceLoopAsync(stoppingToken)).Append(flush));
    }

    /// <summary>Tâches laissées « en cours » par l'arrêt précédent de cette instance (même nom) : elles repartent.</summary>
    private async Task RecoverOwnJobsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var prefix = options.Value.Jobs.WorkerName + ":";
        var n = await db.Jobs.Where(j => j.Status == JobStatus.Running && j.WorkerId!.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.WorkerId, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null).SetProperty(j => j.Attempts, j => j.Attempts - 1), ct);
        if (n > 0) logger.LogInformation("{Count} tâche(s) interrompue(s) par l'arrêt reprennent", n);
    }

    private async Task LaneLoopAsync(string lane, CancellationToken ct)
    {
        var running = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            try
            {
                var capacity = Capacity(lane);
                while (running.Count < capacity && await TryClaimAsync(lane, capacity, ct) is { } id)
                    running.Add(Task.Run(() => RunAsync(id, ct), CancellationToken.None));
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "File de tâches {Lane} : réservation impossible", lane);
            }
            // Réveil : nouvelle tâche, fin d'une tâche (place libre), ou relève périodique (tâches différées, autres instances).
            await WaitAsync(_signals[lane], PollInterval, running, ct);
        }
        await Task.WhenAll(running);
    }

    /// <summary>
    /// Réservation atomique de la prochaine tâche de la file, si la file n'est pas pleine (toutes instances comprises).
    /// Une seule instruction SQL : deux consommateurs ne peuvent ni prendre la même tâche ni dépasser la capacité.
    /// </summary>
    private async Task<Guid?> TryClaimAsync(string lane, int capacity, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "Jobs"
            SET "Status" = 'Running', "WorkerId" = $worker, "LeaseUntil" = $lease, "StartedAt" = $now,
                "Attempts" = "Attempts" + 1, "Progress" = NULL, "CancelRequested" = 0
            WHERE "Id" = (
                SELECT "Id" FROM "Jobs"
                WHERE "Lane" = $lane AND "Status" = 'Queued' AND "NotBefore" <= $now
                ORDER BY "Priority" DESC, "CreatedAt"
                LIMIT 1)
              AND (SELECT COUNT(*) FROM "Jobs" WHERE "Lane" = $lane AND "Status" = 'Running') < $capacity
            RETURNING "Id"
            """;
        var now = time.GetUtcNow();
        Add(command, "$worker", _workerId);
        Add(command, "$lease", Dates.ConvertToProvider(now + Lease));
        Add(command, "$now", Dates.ConvertToProvider(now));
        Add(command, "$lane", lane);
        Add(command, "$capacity", capacity);
        var id = await command.ExecuteScalarAsync(ct);
        return id is string s ? Guid.Parse(s) : null;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private async Task RunAsync(Guid id, CancellationToken stopping)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id, CancellationToken.None);
        var input = job.DependsOnId is { } dep
            ? await db.Jobs.Where(j => j.Id == dep).Select(j => j.Output).FirstOrDefaultAsync(CancellationToken.None)
            : null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[id] = cts;
        // Portée à part pour le travail de la tâche : son suivi par le moteur n'interfère pas avec ses entités.
        using var work = scopes.CreateScope();
        var ctx = new JobContext(job, input is null ? null : JsonNode.Parse(input) as JsonObject, work.ServiceProvider, this, cts.Token);
        using var beat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(id, ctx, cts, beat.Token);

        Outcome outcome;
        try
        {
            if (!_handlers.TryGetValue(job.Kind, out var handler)) throw new InvalidOperationException(Tx.Key("jobs.log.unknownKind", ("kind", job.Kind)));
            if (job.Attempts > 1) Log(id, "info", Tx.Key("jobs.log.attempt", ("n", job.Attempts), ("max", job.MaxAttempts)));
            await handler.RunAsync(ctx);
            outcome = new Outcome(JobStatus.Succeeded);
        }
        catch (JobDeferredException ex)
        {
            Log(id, "info", Tx.Key("jobs.log.deferred", ("reason", ex.Message)));
            outcome = new Outcome(JobStatus.Queued, NotBefore: ex.Until, Error: ex.Message, Deferred: true);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Arrêt du serveur : la tâche reprendra au démarrage, sans compter cette tentative.
            outcome = new Outcome(JobStatus.Queued, Deferred: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log(id, "warn", Tx.Key("jobs.log.canceled"));
            outcome = new Outcome(JobStatus.Canceled, Notify: true);
        }
        catch (JobAbortedException ex)
        {
            Log(id, "info", ex.Message);
            outcome = new Outcome(JobStatus.Canceled, Error: ex.Message);
        }
        catch (Exception ex)
        {
            Log(id, "error", ex.Message);
            if (ex is not (InvalidOperationException or JsonException)) logger.LogWarning(ex, "Tâche {Kind} {Id} en échec", job.Kind, id);
            outcome = job.Attempts < job.MaxAttempts
                ? new Outcome(JobStatus.Queued, NotBefore: time.GetUtcNow() + RetryDelay * job.Attempts, Error: ex.Message)
                : new Outcome(JobStatus.Failed, Error: ex.Message);
        }
        finally
        {
            _running.TryRemove(id, out _);
            await beat.CancelAsync();
            await heartbeat;
        }

        try
        {
            await CompleteAsync(db, job, ctx, outcome);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Le bail expirera et la tâche sera relancée.
            logger.LogError(ex, "Tâche {Id} : fin non enregistrée", id);
        }
    }

    private sealed record Outcome(JobStatus Status, DateTimeOffset? NotBefore = null, string? Error = null, bool Deferred = false, bool Notify = false);

    private async Task CompleteAsync(SonNuageDbContext db, Job job, JobContext ctx, Outcome outcome)
    {
        var now = time.GetUtcNow();
        var ct = CancellationToken.None;
        var mine = db.Jobs.Where(j => j.Id == job.Id && j.WorkerId == _workerId && j.Status == JobStatus.Running);
        var error = outcome.Error is { Length: > 2000 } e ? e[..2000] : outcome.Error;
        int n;
        if (outcome.Status == JobStatus.Queued)
        {
            n = await mine.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.NotBefore, outcome.NotBefore ?? now).SetProperty(j => j.Error, error)
                .SetProperty(j => j.WorkerId, (string?)null).SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Attempts, j => outcome.Deferred ? j.Attempts - 1 : j.Attempts), ct);
            Wake(job.Lane);
            return;
        }

        var output = ctx.Output.Count == 0 ? null : ctx.Output.ToJsonString();
        n = await mine.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, outcome.Status)
            .SetProperty(j => j.Output, output).SetProperty(j => j.Error, error)
            .SetProperty(j => j.Progress, outcome.Status == JobStatus.Succeeded ? 1 : ctx.Progress)
            .SetProperty(j => j.FinishedAt, now).SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
        Wake(job.Lane);
        // Bail perdu entre-temps (instance jugée morte) : la tâche a été rendue à la file, sa nouvelle exécution décidera.
        if (n == 0) return;

        if (outcome.Status == JobStatus.Succeeded)
        {
            var lanes = await db.Jobs.Where(j => j.DependsOnId == job.Id && j.Status == JobStatus.Pending).Select(j => j.Lane).Distinct().ToListAsync(ct);
            await db.Jobs.Where(j => j.DependsOnId == job.Id && j.Status == JobStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.NotBefore, now), ct);
            foreach (var lane in lanes) Wake(lane);
        }
        else
        {
            await CancelDependentsAsync(db, job.Id, ct);
            if (outcome.Notify) await NotifyCanceledAsync([(job.Kind, job.SubjectId)], ct);
        }
    }

    private async Task CancelDependentsAsync(SonNuageDbContext db, Guid jobId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var reason = Tx.Key("jobs.log.dependencyFailed");
        for (var ids = new List<Guid> { jobId }; ids.Count > 0;)
        {
            var next = await db.Jobs.Where(j => j.DependsOnId != null && ids.Contains(j.DependsOnId.Value) && j.Status == JobStatus.Pending)
                .Select(j => j.Id).ToListAsync(ct);
            await db.Jobs.Where(j => next.Contains(j.Id)).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.Error, reason).SetProperty(j => j.FinishedAt, now), ct);
            ids = next;
        }
    }

    /// <summary>Renouvelle le bail, enregistre l'avancement et relève les demandes d'annulation venues d'ailleurs.</summary>
    private async Task HeartbeatAsync(Guid id, JobContext ctx, CancellationTokenSource job, CancellationToken stop)
    {
        double? lastProgress = null;
        try
        {
            while (true)
            {
                await Task.Delay(Heartbeat, time, stop);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var progress = ctx.Progress;
                var lease = time.GetUtcNow() + Lease;
                await db.Jobs.Where(j => j.Id == id && j.WorkerId == _workerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, lease).SetProperty(j => j.Progress, progress), stop);
                lastProgress = progress;
                if (await db.Jobs.AnyAsync(j => j.Id == id && j.CancelRequested, stop)) await job.CancelAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Tâche {Id} : bail non renouvelé (dernier avancement {Progress})", id, lastProgress);
        }
    }

    /// <summary>Tâches dont le bail a expiré (instance arrêtée brutalement) et ménage des vieilles suites.</summary>
    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var now = time.GetUtcNow();
                var expired = await db.Jobs.Where(j => j.Status == JobStatus.Running && j.LeaseUntil < now)
                    .Select(j => new { j.Id, j.Attempts, j.Lane }).ToListAsync(ct);
                foreach (var j in expired)
                {
                    var giveUp = j.Attempts >= MaxCrashRestarts;
                    await db.Jobs.Where(x => x.Id == j.Id && x.Status == JobStatus.Running && x.LeaseUntil < now)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, giveUp ? JobStatus.Failed : JobStatus.Queued)
                            .SetProperty(x => x.WorkerId, (string?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                            .SetProperty(x => x.NotBefore, now).SetProperty(x => x.Error, giveUp ? Tx.Key("jobs.log.lost") : null), ct);
                    Log(j.Id, "warn", Tx.Key(giveUp ? "jobs.log.lost" : "jobs.log.resumed"));
                    if (giveUp) await CancelDependentsAsync(db, j.Id, ct);
                    Wake(j.Lane);
                }

                if (now >= nextCleanup)
                {
                    nextCleanup = now + TimeSpan.FromHours(1);
                    var cutoff = now - TimeSpan.FromDays(Math.Max(1, options.Value.Jobs.RetentionDays));
                    var removed = await db.JobSuites
                        .Where(s => s.CreatedAt < cutoff && !s.Jobs.Any(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.FinishedAt >= cutoff))
                        .ExecuteDeleteAsync(ct);
                    if (removed > 0) logger.LogInformation("{Count} suite(s) de tâches terminées supprimée(s)", removed);
                }
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Maintenance des tâches");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), time, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task FlushLogsAsync(CancellationToken ct)
    {
        var batch = new List<JobLog>();
        while (true)
        {
            try
            {
                if (!await _logs.Reader.WaitToReadAsync(ct)) break;
                // Petit délai : les lignes émises ensemble partent dans la même écriture.
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
            }
            while (_logs.Reader.TryRead(out var log)) batch.Add(log);
            if (batch.Count > 0)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                    // Tâche supprimée entre-temps : ses lignes sont perdues sans gêner les autres.
                    var ids = batch.Select(l => l.JobId).Distinct().ToList();
                    var known = (await db.Jobs.Where(j => ids.Contains(j.Id)).Select(j => j.Id).ToListAsync(CancellationToken.None)).ToHashSet();
                    db.JobLogs.AddRange(batch.Where(l => known.Contains(l.JobId)));
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Journal des tâches : {Count} ligne(s) perdue(s)", batch.Count);
                }
                batch.Clear();
            }
            if (ct.IsCancellationRequested) break;
        }
    }

    private static async Task WaitAsync(Channel<bool> wake, TimeSpan delay, IEnumerable<Task> running, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(delay);
        var read = wake.Reader.ReadAsync(cts.Token).AsTask();
        await Task.WhenAny(running.Append(read));
        await cts.CancelAsync();
        try
        {
            await read;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
