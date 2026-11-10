using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SonNuage.Server.Tests;

/// <summary>Flux en direct du volet Tâches. La relève périodique est d'une minute ici : ce qui arrive vite a été poussé.</summary>
public sealed class JobStreamTests(JobsFactory factory) : IClassFixture<JobsFactory>
{
    private const string Url = "/api/admin/jobs/events";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url + query);
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    /// <summary>Lit le flux jusqu'à ce que <paramref name="done"/> ait vu ce qu'il attendait.</summary>
    private static async Task ReadUntilAsync(HttpResponseMessage response, Func<ServerSentEvent, bool> done)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stream = await response.Content.ReadAsStreamAsync(Ct);
        await foreach (var e in ServerSentEvents.ReadAsync(stream, TimeSpan.FromSeconds(15), timeout.Token))
            if (done(e)) return;
        Assert.Fail("Flux terminé trop tôt");
    }

    [Fact]
    public async Task The_live_stream_is_for_admins_only()
    {
        using var anonymous = await OpenAsync(factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("watcher", "watcher-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        using var user = await OpenAsync(await factory.ClientAsync("watcher", "watcher-password"));
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
    }

    [Fact]
    public async Task The_live_stream_starts_with_the_current_state_and_is_not_buffered()
    {
        using var response = await OpenAsync(await factory.AdminClientAsync(), "?status=active&limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        JobsOverviewDto? overview = null;
        await ReadUntilAsync(response, e => (overview = e.Event == JobStreamEvents.Overview ? JsonSerializer.Deserialize<JobsOverviewDto>(e.Data, Json) : null) is not null);
        Assert.Equal(JobLanes.All, overview!.Lanes.Select(l => l.Lane));
        Assert.Equal(5, overview.Suites.Limit);
    }

    [Fact]
    public async Task Job_changes_and_new_log_lines_are_pushed()
    {
        var admin = await factory.AdminClientAsync();
        var plan = new JobPlan("test", "en direct").Then(ProbeJob.Definition, "sonde", new { delay = 300 });
        using (var scope = factory.Services.CreateScope())
            await factory.Services.GetRequiredService<JobEngine>().EnqueueAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), plan, Ct);
        var jobId = plan.Last.Id;

        using var response = await OpenAsync(admin, $"?suite={plan.Suite.Id}&job={jobId}&after=0");
        var lines = new List<JobLogDto>();
        string? status = null, suiteStatus = null;
        await ReadUntilAsync(response, e =>
        {
            switch (e.Event)
            {
                case JobStreamEvents.Logs:
                    lines.AddRange(JsonSerializer.Deserialize<List<JobLogDto>>(e.Data, Json)!);
                    break;
                case JobStreamEvents.Job:
                    status = JsonSerializer.Deserialize<JobDetailDto>(e.Data, Json)!.Job.Status;
                    break;
                case JobStreamEvents.Suite:
                    suiteStatus = JsonSerializer.Deserialize<PagedResult<JobDto>>(e.Data, Json)!.Items.Single().Status;
                    break;
            }
            return status == "Succeeded" && suiteStatus == "Succeeded" && lines.Any(l => l.Message == "run 1");
        });
        // Chaque ligne une seule fois, dans l'ordre.
        Assert.Equal(lines.Select(l => l.Id).Order().Distinct(), lines.Select(l => l.Id));
    }

    [Fact]
    public async Task A_quiet_stream_sends_heartbeats()
    {
        using var response = await OpenAsync(await factory.AdminClientAsync());
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        // Sans battement de cœur en 10 s, la lecture est interrompue et le test échoue.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
            if (line.StartsWith(':')) return;
        Assert.Fail("Flux terminé trop tôt");
    }
}
