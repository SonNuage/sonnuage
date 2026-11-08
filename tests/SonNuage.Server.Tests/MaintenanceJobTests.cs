using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Jobs;
using Microsoft.AspNetCore.Hosting;

namespace SonNuage.Server.Tests;

/// <summary>Elasticsearch et yt-dlp configurés mais injoignables : leurs tâches échouent vite, avec leur journal.</summary>
public sealed class MaintenanceFactory : SonNuageFactory
{
    public MaintenanceFactory()
    {
        JobEngine.RetryDelay = TimeSpan.FromMilliseconds(100);
        JobEngine.PollInterval = TimeSpan.FromMilliseconds(200);
    }

    protected override void ConfigureMore(IWebHostBuilder builder)
    {
        builder.UseSetting("SonNuage:Search:Url", "http://127.0.0.1:1");
        builder.UseSetting("SonNuage:YtDlpPath", Path.Combine(DataPath, "pas-de-yt-dlp"));
    }
}

public sealed class MaintenanceJobTests(MaintenanceFactory factory) : IClassFixture<MaintenanceFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>La suite la plus récente du type donné, une fois dans l'un des états attendus.</summary>
    private static async Task<JobSuiteDto> SuiteAsync(HttpClient admin, string kind, DateTimeOffset after, params string[] statuses)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var overview = await admin.GetFromJsonAsync<JobsOverviewDto>($"/api/admin/jobs?kind={kind}", Ct);
            var suite = overview!.Suites.Items.Where(s => s.CreatedAt >= after).MaxBy(s => s.CreatedAt);
            if (suite is not null && statuses.Contains(suite.Status)) return suite;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"{kind} : {suite?.Status ?? "absente"}");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<(JobDto Job, List<JobLogDto> Logs)> OnlyJobAsync(HttpClient admin, JobSuiteDto suite)
    {
        var job = Assert.Single((await admin.GetFromJsonAsync<PagedResult<JobDto>>($"/api/admin/jobs/suites/{suite.Id}", Ct))!.Items);
        // Le journal est écrit en base par lots : on lui laisse le temps d'arriver.
        for (var i = 0; ; i++)
        {
            var logs = (await admin.GetFromJsonAsync<List<JobLogDto>>($"/api/admin/jobs/{job.Id}/logs", Ct))!;
            if (logs.Count > 0 || i == 50) return (job, logs);
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_full_scan_requested_by_the_admin_runs_as_a_job_with_its_log()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/scan", null, Ct)).StatusCode);

        var suite = await SuiteAsync(admin, JobSuiteKinds.LibraryScan, before, "Succeeded");
        var (job, logs) = await OnlyJobAsync(admin, suite);
        Assert.Equal(JobLanes.Library, job.Lane);
        Assert.NotEmpty(logs);
        var stats = await admin.GetFromJsonAsync<LibraryStatsDto>("/api/admin/stats", Ct);
        Assert.False(stats!.Scan.Running);
        Assert.Equal(3, stats.Scan.FilesSeen); // la copie « (1) » de Windows est sautée
    }

    [Fact]
    public async Task Purging_the_transcode_cache_is_a_job()
    {
        var admin = await factory.AdminClientAsync();
        var cache = Path.Combine(factory.DataPath, "cache", "transcode", "ab");
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(cache, "x.mp3"), new byte[1000], Ct);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.DeleteAsync("/api/admin/cache/transcode", Ct)).StatusCode);

        var suite = await SuiteAsync(admin, JobSuiteKinds.TranscodePurge, before, "Succeeded");
        var (_, logs) = await OnlyJobAsync(admin, suite);
        Assert.NotEmpty(logs);
        Assert.False(File.Exists(Path.Combine(cache, "x.mp3")));
    }

    [Fact]
    public async Task A_search_reindex_is_a_job_retried_then_failed_when_elasticsearch_is_down()
    {
        var admin = await factory.AdminClientAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/search/reindex", null, Ct)).StatusCode);

        var suite = await SuiteAsync(admin, JobSuiteKinds.SearchReindex, before, "Failed");
        var (job, logs) = await OnlyJobAsync(admin, suite);
        Assert.Equal(job.MaxAttempts, job.Attempts);
        Assert.Contains(logs, l => l.Level == "error");
    }

    [Fact]
    public async Task Syncing_a_subscription_is_a_job_and_its_failure_is_kept()
    {
        var admin = await factory.AdminClientAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var created = await admin.PostAsJsonAsync("/api/admin/youtube/subscriptions",
            new CreateSubscriptionRequest("https://www.youtube.com/playlist?list=PLtest", false, true), Ct);
        created.EnsureSuccessStatusCode();

        var suite = await SuiteAsync(admin, JobSuiteKinds.SubscriptionSync, before, "Failed");
        var (job, _) = await OnlyJobAsync(admin, suite);
        Assert.Equal(JobLanes.Sync, job.Lane);
        Assert.NotNull(job.Error);
    }
}
