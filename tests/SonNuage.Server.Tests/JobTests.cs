using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Youtube;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SonNuage.Server.Tests;

/// <summary>Tâches de test : leur comportement dépend de leurs paramètres.</summary>
public sealed class ProbeJob : IJobHandler
{
    public static readonly JobKind Definition = new("test.probe", JobLanes.AcoustId, MaxAttempts: 2);
    private static int _running;
    public static int MaxConcurrent;
    public static readonly ConcurrentDictionary<Guid, int> Runs = new();

    public JobKind Kind => Definition;

    public async Task RunAsync(JobContext ctx)
    {
        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref MaxConcurrent, now);
        try
        {
            var run = Runs.AddOrUpdate(ctx.JobId, 1, (_, n) => n + 1);
            ctx.Info($"run {run}");
            if (ctx.Get<bool>("block")) await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            await Task.Delay(ctx.Get<int>("delay"), ctx.CancellationToken);
            if (ctx.Get<int>("failTimes") >= run) throw new InvalidOperationException($"échec {run}");
            ctx.Set("value", (ctx.Input["value"]?.GetValue<int>() ?? 0) + ctx.Get<int>("add"));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

public sealed class JobsFactory : SonNuageFactory
{
    public JobsFactory()
    {
        JobEngine.RetryDelay = TimeSpan.FromMilliseconds(100);
        JobEngine.PollInterval = TimeSpan.FromMilliseconds(200);
    }

    protected override void ConfigureMore(IWebHostBuilder builder) =>
        builder.ConfigureServices(s => s.AddSingleton<IJobHandler, ProbeJob>());
}

public sealed class JobTests(JobsFactory factory) : IClassFixture<JobsFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JobSuite> EnqueueAsync(JobPlan plan)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        return await factory.Services.GetRequiredService<JobEngine>().EnqueueAsync(db, plan, Ct);
    }

    private async Task<JobDto> WaitAsync(HttpClient admin, Guid jobId, params string[] statuses)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = (await admin.GetFromJsonAsync<JobDetailDto>($"/api/admin/jobs/{jobId}", Ct))!.Job;
            if (statuses.Contains(job.Status)) return job;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"{job.Title} : {job.Status}");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_lane_never_runs_more_jobs_than_its_capacity()
    {
        var admin = await factory.AdminClientAsync();
        ProbeJob.MaxConcurrent = 0;
        var suites = new List<JobSuite>();
        for (var i = 0; i < 4; i++)
            suites.Add(await EnqueueAsync(new JobPlan("test", $"capacité {i}").Then(ProbeJob.Definition, "probe", new { delay = 150 })));

        foreach (var suite in suites)
        {
            var page = await admin.GetFromJsonAsync<PagedResult<JobDto>>($"/api/admin/jobs/suites/{suite.Id}", Ct);
            await WaitAsync(admin, page!.Items[0].Id, "Succeeded");
        }
        // File « acoustid » : une seule tâche à la fois, même avec quatre suites prêtes ensemble.
        Assert.Equal(1, ProbeJob.MaxConcurrent);
    }

    [Fact]
    public async Task A_suite_passes_outputs_along_and_stops_after_a_failure()
    {
        var admin = await factory.AdminClientAsync();
        var plan = new JobPlan("test", "chaîne")
            .Then(ProbeJob.Definition, "un", new { add = 1 })
            .Then(ProbeJob.Definition, "deux", new { add = 10 });
        await EnqueueAsync(plan);
        var second = await WaitAsync(admin, plan.Last.Id, "Succeeded");
        var detail = await admin.GetFromJsonAsync<JobDetailDto>($"/api/admin/jobs/{second.Id}", Ct);
        Assert.Contains("\"value\":11", detail!.Output);

        var failing = new JobPlan("test", "échec")
            .Then(ProbeJob.Definition, "casse", new { failTimes = 5 })
            .Then(ProbeJob.Definition, "jamais");
        await EnqueueAsync(failing);
        var first = await WaitAsync(admin, failing.Jobs[0].Id, "Failed");
        Assert.Equal(2, first.Attempts);
        Assert.Equal("Canceled", (await WaitAsync(admin, failing.Last.Id, "Canceled")).Status);

        var overview = await admin.GetFromJsonAsync<JobsOverviewDto>("/api/admin/jobs?status=failed", Ct);
        Assert.Contains(overview!.Suites.Items, s => s.Id == failing.Suite.Id && s.Status == "Failed" && s.Failed == 1 && s.Canceled == 1);
        Assert.Contains(overview.Lanes, l => l.Lane == JobLanes.AcoustId && l.Capacity == 1);
    }

    [Fact]
    public async Task A_failed_job_is_retried_then_can_be_retried_by_hand()
    {
        var admin = await factory.AdminClientAsync();
        var plan = new JobPlan("test", "nouvelle tentative").Then(ProbeJob.Definition, "instable", new { failTimes = 1 });
        await EnqueueAsync(plan);
        var job = await WaitAsync(admin, plan.Last.Id, "Succeeded");
        Assert.Equal(2, job.Attempts);

        var broken = new JobPlan("test", "relance").Then(ProbeJob.Definition, "cassé", new { failTimes = 2 }).Then(ProbeJob.Definition, "suite");
        await EnqueueAsync(broken);
        await WaitAsync(admin, broken.Last.Id, "Canceled");
        // Troisième exécution : réussit, et la tâche suivante repart avec elle.
        (await admin.PostAsync($"/api/admin/jobs/{broken.Jobs[0].Id}/retry", null, Ct)).EnsureSuccessStatusCode();
        await WaitAsync(admin, broken.Last.Id, "Succeeded");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/jobs/{broken.Jobs[0].Id}/retry", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_running_job_can_be_canceled_and_its_logs_are_readable_live()
    {
        var admin = await factory.AdminClientAsync();
        var plan = new JobPlan("test", "annulation").Then(ProbeJob.Definition, "bloquée", new { block = true }).Then(ProbeJob.Definition, "après");
        await EnqueueAsync(plan);
        var running = await WaitAsync(admin, plan.Jobs[0].Id, "Running");

        List<JobLogDto>? logs = null;
        for (var i = 0; i < 100 && logs is not { Count: > 0 }; i++)
        {
            logs = await admin.GetFromJsonAsync<List<JobLogDto>>($"/api/admin/jobs/{running.Id}/logs", Ct);
            await Task.Delay(50, Ct);
        }
        Assert.Contains(logs!, l => l.Message == "run 1");
        Assert.Empty((await admin.GetFromJsonAsync<List<JobLogDto>>($"/api/admin/jobs/{running.Id}/logs?after={logs!.Max(l => l.Id)}", Ct))!);

        (await admin.PostAsync($"/api/admin/jobs/{running.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        await WaitAsync(admin, running.Id, "Canceled");
        await WaitAsync(admin, plan.Last.Id, "Canceled");
    }

    [Fact]
    public async Task An_acoustid_rescan_needs_a_key()
    {
        var admin = await factory.AdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/admin/jobs", new CreateJobRequest("acoustid-rescan", null, true), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        response = await admin.PostAsJsonAsync("/api/admin/jobs", new CreateJobRequest("inconnu", null, true), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var user = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/admin/jobs", Ct)).StatusCode);
    }

    [Fact]
    public void A_rescan_carries_its_own_threshold()
    {
        var withThreshold = AcoustIdRescanJob.Plan("t", null, true, 0.75, null);
        Assert.Contains("\"threshold\":0.75", Assert.Single(withThreshold.Jobs).Payload);
        var withDefault = AcoustIdRescanJob.Plan("t", null, true, null, null);
        Assert.Contains("\"threshold\":null", Assert.Single(withDefault.Jobs).Payload);
    }

    [Fact]
    public async Task AcoustId_requests_are_sent_one_at_a_time_a_second_apart()
    {
        var handler = new RecordingHandler();
        var client = new MusicBrainzClient(new SingleClientFactory(handler), factory.Services.GetRequiredService<YoutubeTools>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzClient>.Instance);
        var fp = new AudioFingerprint(200, "AQAA");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.AcoustIdLookupAsync(fp, "key", Ct)));

        Assert.All(results, r => Assert.Equal("rec-1", Assert.Single(r).RecordingId));
        Assert.Equal(1, handler.MaxConcurrent);
        var starts = handler.Starts.Order().ToList();
        for (var i = 1; i < starts.Count; i++)
            Assert.True(starts[i] - starts[i - 1] >= TimeSpan.FromMilliseconds(950), $"écart {starts[i] - starts[i - 1]}");

        handler.Error = true;
        await Assert.ThrowsAsync<AcoustIdException>(() => client.AcoustIdLookupAsync(fp, "key", Ct));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _running;
        public int MaxConcurrent;
        public bool Error;
        public readonly ConcurrentBag<TimeSpan> Starts = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Starts.Add(_clock.Elapsed);
            var now = Interlocked.Increment(ref _running);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref _running);
            var body = Error
                ? """{"status":"error","error":{"code":14,"message":"rate limit exceeded"}}"""
                : """{"status":"ok","results":[{"score":0.97,"recordings":[{"id":"rec-1"}]}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
