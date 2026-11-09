using System.Diagnostics;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Tests;

public class SearchGatewayTests
{
    private static SearchGateway Gateway(int timeoutMs)
    {
        // Port fermé : seule la sonde de reprise s'en sert, les appels testés sont des délégués.
        var options = Options.Create(new SonNuageOptions { Search = new SearchOptions { Url = "http://127.0.0.1:9", TimeoutMilliseconds = timeoutMs } });
        var es = new ElasticsearchClient(new PlainHttpFactory(), options, NullLogger<ElasticsearchClient>.Instance);
        return new SearchGateway(es, options, TimeProvider.System, NullLogger<SearchGateway>.Instance) { IndexReady = true };
    }

    [Fact]
    public async Task Fast_answer_is_used()
    {
        var result = await Gateway(2000).TryAsync("k", _ => Task.FromResult("es"));
        Assert.Equal("es", result);
    }

    [Fact]
    public async Task Slow_answer_falls_back_then_is_served_once_it_arrives()
    {
        // Elasticsearch « lent » : il ne répond que quand le test le décide, quelle que soit la charge de la machine.
        var gateway = Gateway(200);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var first = await gateway.TryAsync("k", _ => answer.Task);
        Assert.Null(first);
        Assert.True(watch.ElapsedMilliseconds >= 150, $"repli au bout de {watch.ElapsedMilliseconds} ms seulement");
        Assert.False(gateway.Degraded);

        // La requête a continué en arrière-plan : la frappe suivante identique profite de son résultat.
        answer.SetResult("es");
        var second = await gateway.TryAsync<string>("k", _ => throw new InvalidOperationException("ne doit pas être rappelé"));
        Assert.Equal("es", second);
    }

    [Fact]
    public async Task Repeated_failures_set_elasticsearch_aside()
    {
        var gateway = Gateway(10_000);
        var calls = 0;
        var first = await gateway.TryAsync<string>("k", _ =>
        {
            calls++;
            throw new HttpRequestException("connection refused");
        });
        Assert.Null(first);
        Assert.Equal(3, calls);
        Assert.True(gateway.Degraded);
        Assert.Equal("degraded", gateway.Status);

        // Mis de côté : réponse immédiate en base, sans même tenter Elasticsearch.
        var watch = Stopwatch.StartNew();
        var next = await gateway.TryAsync("other", _ =>
        {
            calls++;
            return Task.FromResult("es");
        });
        Assert.Null(next);
        Assert.Equal(3, calls);
        Assert.True(watch.ElapsedMilliseconds < 100);
    }

    private sealed class PlainHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("http://127.0.0.1:9/") };
    }
}

/// <summary>Serveur branché sur un vrai Elasticsearch (variable SONNUAGE_TEST_ELASTICSEARCH, ex. http://localhost:9200).</summary>
public sealed class ElasticsearchFactory : SonNuageFactory
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("SONNUAGE_TEST_ELASTICSEARCH");
    public readonly string Index = "sonnuage-test-" + Guid.NewGuid().ToString("N")[..8];

    protected override void ConfigureMore(IWebHostBuilder builder)
    {
        builder.UseSetting("SonNuage:Search:Url", Url ?? "");
        builder.UseSetting("SonNuage:Search:Index", Index);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Url is not null)
        {
            using var http = new HttpClient { BaseAddress = new Uri(Url.TrimEnd('/') + "/") };
            try
            {
                // Suppression par nom exact : Elasticsearch refuse les jokers en DELETE par défaut.
                var names = await http.GetFromJsonAsync<List<Dictionary<string, string>>>($"_cat/indices/{Index}-*?h=index&format=json") ?? [];
                foreach (var name in names.Select(n => n["index"])) await http.DeleteAsync(name);
            }
            catch (HttpRequestException) { }
        }
        await base.DisposeAsync();
    }
}

public sealed class ElasticsearchSearchTests(ElasticsearchFactory factory) : IClassFixture<ElasticsearchFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> ReadyClientAsync()
    {
        Assert.SkipWhen(ElasticsearchFactory.Url is null, "SONNUAGE_TEST_ELASTICSEARCH non défini");
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        for (var i = 0; i < 300; i++)
        {
            var stats = await admin.GetFromJsonAsync<LibraryStatsDto>("/api/admin/stats", Ct);
            // Index prêt, et ses documents visibles (Elasticsearch les rend cherchables après un rafraîchissement).
            if (stats!.SearchEngine == "elasticsearch"
                && (await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=other%20band", Ct))!.Artists.Count > 0) return admin;
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException("Index Elasticsearch jamais prêt");
    }

    [Fact]
    public async Task Typos_are_tolerated()
    {
        var client = await ReadyClientAsync();
        var result = await client.GetFromJsonAsync<SearchResultDto>("/api/search?q=secnd%20sogn", Ct);
        Assert.Equal("Second Song", result!.Tracks.First().Title);
    }

    [Fact]
    public async Task Prefixes_match_as_you_type()
    {
        var client = await ReadyClientAsync();
        var result = await client.GetFromJsonAsync<SearchResultDto>("/api/search?q=oth%20ba", Ct);
        Assert.Contains(result!.Artists, a => a.Name == "Other Band");
    }

    [Fact]
    public async Task Unknown_words_get_a_correction()
    {
        var client = await ReadyClientAsync();
        var result = await client.GetFromJsonAsync<SearchResultDto>("/api/search?q=enocre", Ct);
        Assert.Equal("encore", result!.Correction, ignoreCase: true);
    }

    [Fact]
    public async Task Suggestions_complete_names()
    {
        var client = await ReadyClientAsync();
        var suggestions = await client.GetFromJsonAsync<List<string>>("/api/search/suggest?q=firs", Ct);
        Assert.Contains("First Album", suggestions!);
    }

    [Fact]
    public async Task Suggestions_match_any_word_start()
    {
        var client = await ReadyClientAsync();
        var suggestions = await client.GetFromJsonAsync<List<string>>("/api/search/suggest?q=band", Ct);
        Assert.Contains("Other Band", suggestions!);
    }
}
