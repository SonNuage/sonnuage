using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Api;

/// <summary>
/// Volet Tâches en direct (Server-Sent Events) : à l'ouverture puis à chaque changement signalé par le moteur
/// (<see cref="JobChanges"/>), le flux renvoie ce qui a changé parmi les files et la page de suites, la suite ouverte,
/// la tâche ouverte et les nouvelles lignes de son journal. Une relève périodique couvre les changements des autres
/// instances ; un commentaire « : ping » toutes les 15 s garde la connexion ouverte à travers les proxys (Cloudflare).
/// </summary>
public static class JobEventStream
{
    internal static TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    internal static TimeSpan Backstop = TimeSpan.FromSeconds(5);
    /// <summary>Attente après un signal : les changements en rafale (journal, avancement) partent ensemble.</summary>
    internal static TimeSpan Coalesce = TimeSpan.FromMilliseconds(300);

    /// <summary>Ce que le client a déjà reçu : seul ce qui diffère repart.</summary>
    private sealed class Sent(long after)
    {
        public readonly Dictionary<string, string> Last = [];
        public long After = after;
    }

    public static async Task StreamAsync(HttpContext http, string? status, string? kind, string? q, int? limit, Guid? suite, int? suiteLimit,
        Guid? job, long? after, JobChanges changes, IServiceScopeFactory scopes, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, TimeProvider time)
    {
        var ct = http.RequestAborted;
        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        // Nginx et apparentés : pas de mise en tampon du flux.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(ct);

        using var subscription = changes.Subscribe();
        var sent = new Sent(after ?? 0);
        var options = json.Value.SerializerOptions;
        async Task<bool> SendAsync()
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var engine = scope.ServiceProvider.GetRequiredService<JobEngine>();
            var wrote = await WriteIfChangedAsync(response, JobStreamEvents.Overview,
                await JobEndpoints.OverviewAsync(db, engine, status, kind, q, 0, limit, ct), options, sent, ct);
            if (suite is { } suiteId)
                wrote |= await WriteIfChangedAsync(response, JobStreamEvents.Suite, await JobEndpoints.SuiteJobsAsync(db, suiteId, 0, suiteLimit, ct), options, sent, ct);
            if (job is { } jobId)
            {
                if (await JobEndpoints.DetailAsync(db, jobId, ct) is { } detail)
                    wrote |= await WriteIfChangedAsync(response, JobStreamEvents.Job, detail, options, sent, ct);
                List<JobLogDto> lines;
                do
                {
                    lines = await JobEndpoints.LogsAsync(db, jobId, sent.After, ct);
                    if (lines.Count == 0) break;
                    await WriteAsync(response, JobStreamEvents.Logs, JsonSerializer.Serialize(lines, options), ct);
                    sent.After = lines[^1].Id;
                    wrote = true;
                } while (lines.Count >= 500);
            }
            await response.Body.FlushAsync(ct);
            return wrote;
        }

        try
        {
            await SendAsync();
            var lastWrite = time.GetUtcNow();
            var nextCheck = lastWrite + Backstop;
            while (!ct.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                var wait = Min(nextCheck - now, lastWrite + Heartbeat - now);
                var signaled = await WaitAsync(subscription.Reader, wait, ct);
                if (signaled) await Task.Delay(Coalesce, time, ct);
                now = time.GetUtcNow();
                if (signaled || now >= nextCheck)
                {
                    // Les signaux arrivés pendant l'attente sont couverts par cette relecture.
                    subscription.Reader.TryRead(out _);
                    if (await SendAsync()) lastWrite = time.GetUtcNow();
                    nextCheck = time.GetUtcNow() + Backstop;
                }
                if (time.GetUtcNow() - lastWrite >= Heartbeat)
                {
                    await response.WriteAsync(": ping\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    lastWrite = time.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client parti.
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => TimeSpan.FromTicks(Math.Max(0, Math.Min(a.Ticks, b.Ticks)));

    private static async Task<bool> WriteIfChangedAsync<T>(HttpResponse response, string name, T value, JsonSerializerOptions options, Sent sent, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(value, options);
        if (sent.Last.TryGetValue(name, out var last) && last == data) return false;
        sent.Last[name] = data;
        await WriteAsync(response, name, data, ct);
        return true;
    }

    // JSON sur une ligne : les sauts de ligne des textes y sont échappés.
    private static Task WriteAsync(HttpResponse response, string name, string data, CancellationToken ct) =>
        response.WriteAsync($"event: {name}\ndata: {data}\n\n", ct);

    /// <summary>Attend un signal au plus <paramref name="delay"/>. Vrai si un changement a été signalé.</summary>
    private static async Task<bool> WaitAsync(ChannelReader<bool> reader, TimeSpan delay, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(delay);
        try
        {
            return await reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
