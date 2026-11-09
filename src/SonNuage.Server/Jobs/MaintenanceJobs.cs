using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Search;
using SonNuage.Server.Streaming;
using SonNuage.Server.Youtube;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Traitements longs de la bibliothèque confiés au moteur de tâches : ils gagnent le journal, l'annulation et
/// l'historique du volet Tâches. Les services qui les déclenchent (minuteries, surveillance des dossiers, admin)
/// ne font plus que demander la tâche (<see cref="RequestAsync"/>), sans doublon tant qu'elle attend son tour.
/// </summary>
public static class MaintenanceJobs
{
    public static JobPlan Scan() =>
        new JobPlan(JobSuiteKinds.LibraryScan, Tx.Key("jobs.suites.scan")).Then(JobKinds.LibraryScan, Tx.Key("jobs.suites.scan"));

    public static JobPlan Reindex() =>
        new JobPlan(JobSuiteKinds.SearchReindex, Tx.Key("jobs.suites.reindex")).Then(JobKinds.SearchReindex, Tx.Key("jobs.suites.reindex"));

    public static JobPlan PurgeTranscodeCache() =>
        new JobPlan(JobSuiteKinds.TranscodePurge, Tx.Key("jobs.suites.purge")).Then(JobKinds.TranscodePurge, Tx.Key("jobs.suites.purge"));

    public static JobPlan DiscoveryRefresh() =>
        new JobPlan(JobSuiteKinds.DiscoveryRefresh, Tx.Key("jobs.suites.discovery")).Then(JobKinds.DiscoveryRefresh, Tx.Key("jobs.suites.discovery"));

    public static JobPlan Sync(Guid subscriptionId, string title) =>
        new JobPlan(JobSuiteKinds.SubscriptionSync, Tx.Key("jobs.suites.sync", ("title", title)))
            .Then(JobKinds.SubscriptionSync, Tx.Key("jobs.suites.sync", ("title", title)), subjectId: subscriptionId);

    /// <summary>
    /// Demande la tâche depuis un service singleton. Le moteur est résolu à l'appel, pas à la construction : il dépend
    /// des gestionnaires de tâches, qui dépendent eux-mêmes de ces services.
    /// </summary>
    public static async Task<JobSuite?> RequestAsync(this IServiceScopeFactory scopes, JobPlan plan, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<JobEngine>();
        return await engine.EnqueueOnceAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), plan, ct);
    }
}

/// <summary>Scan complet de la bibliothèque (voir <see cref="ScanCoordinator.RunFullScanAsync"/>).</summary>
public sealed class LibraryScanJob : IJobHandler
{
    public JobKind Kind => JobKinds.LibraryScan;

    public async Task RunAsync(JobContext ctx)
    {
        // Résolus à l'exécution : ScanCoordinator dépend (de loin) du moteur, qui dépend des gestionnaires.
        var scans = ctx.Services.GetRequiredService<ScanCoordinator>();
        var known = await ctx.Services.GetRequiredService<SonNuageDbContext>().Tracks.CountAsync(ctx.CancellationToken);
        var r = await scans.RunFullScanAsync(p => ctx.Report((double)p.FilesSeen / Math.Max(known, p.FilesSeen + 1)), ctx.CancellationToken);
        ctx.Set("result", r);
        ctx.Info(Tx.Key("jobs.maintenance.scanned", ("seen", r.FilesSeen), ("added", r.Added), ("updated", r.Updated), ("removed", r.Removed)));
    }
}

/// <summary>Reconstruction de l'index Elasticsearch. En cas d'échec : nouvelles tentatives du moteur.</summary>
public sealed class SearchReindexJob : IJobHandler
{
    public JobKind Kind => JobKinds.SearchReindex;

    public async Task RunAsync(JobContext ctx)
    {
        var indexer = ctx.Services.GetRequiredService<SearchIndexer>();
        if (!indexer.Configured)
        {
            ctx.Info(Tx.Key("jobs.maintenance.noSearch"));
            return;
        }
        var (documents, elapsed) = await indexer.ReindexAsync(ctx.CancellationToken);
        ctx.Info(Tx.Key("jobs.maintenance.reindexed", ("count", documents), ("ms", (int)elapsed.TotalMilliseconds)));
    }
}

/// <summary>Vide le cache de transcodage (les fichiers en cours d'écriture restent).</summary>
public sealed class TranscodePurgeJob(TranscodeService transcoder) : IJobHandler
{
    public JobKind Kind => JobKinds.TranscodePurge;

    public Task RunAsync(JobContext ctx)
    {
        var (files, bytes) = transcoder.ClearCache(ctx.CancellationToken);
        ctx.Info(Tx.Key("jobs.maintenance.purged", ("count", files), ("mb", Math.Round(bytes / 1048576.0, 1))));
        return Task.CompletedTask;
    }
}

/// <summary>Lecture d'un abonnement YouTube : ses nouvelles vidéos rejoignent la file d'import.</summary>
public sealed class SubscriptionSyncJob(TimeProvider time) : IJobHandler
{
    public JobKind Kind => JobKinds.SubscriptionSync;

    public async Task RunAsync(JobContext ctx)
    {
        var importer = ctx.Services.GetRequiredService<YoutubeImporter>();
        // Pas de synchronisation pendant que YouTube nous bride : elle échouerait aussi.
        if (importer.HoldUntil is { } hold && hold > time.GetUtcNow()) throw new JobDeferredException(hold, Tx.Key("jobs.maintenance.youtubeHold"));
        var subscriptionId = ctx.SubjectId ?? throw new JobAbortedException(Tx.Key("errors.yt.subNotFound"));
        var outcome = await ctx.Services.GetRequiredService<YoutubeSyncService>().SyncAsync(subscriptionId, ctx.CancellationToken);
        if (outcome.Error is { } error) throw new InvalidOperationException(error);
        ctx.Info(Tx.Key("jobs.maintenance.synced", ("queued", outcome.Queued), ("skipped", outcome.Skipped)));
    }
}

/// <summary>Actualisation de la découverte par artiste (voir <see cref="Discovery.DiscoveryService"/>).</summary>
public sealed class DiscoveryRefreshJob(Discovery.DiscoveryService discovery) : IJobHandler
{
    public JobKind Kind => JobKinds.DiscoveryRefresh;

    public Task RunAsync(JobContext ctx) => discovery.RefreshAsync(ctx);
}
