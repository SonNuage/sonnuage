using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonNuage.Server.Radio;
using SonNuage.Server.Search;

namespace SonNuage.Server.Tests;

/// <summary>
/// Option sémantique d'Elasticsearch (champ semantic_text relié à un endpoint d'inférence), contre un faux
/// Elasticsearch : mapping, licence, combinaison avec la recherche textuelle et texte décrit pour l'inférence.
/// </summary>
public sealed class SemanticSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ElasticsearchClient Es, FakeElasticsearch Fake) Client(string license, string? inferenceId = "e5-local")
    {
        var fake = new FakeElasticsearch(license);
        var options = Options.Create(new SonNuageOptions
        {
            Search = new SearchOptions { Url = "http://es.test:9200", Index = "sonnuage", SemanticInferenceId = inferenceId },
        });
        return (new ElasticsearchClient(fake, options, NullLogger<ElasticsearchClient>.Instance), fake);
    }

    [Fact]
    public async Task With_an_enterprise_or_trial_license_the_index_maps_a_semantic_field()
    {
        var (es, fake) = Client("enterprise");

        await es.EnsureIndexAsync(Ct);

        var (_, path, body) = Assert.Single(fake.Requests, r => r.Method == "PUT");
        Assert.Equal("sonnuage-v2-semantic", path);
        var semantic = JsonNode.Parse(body)!["mappings"]!["properties"]!["semantic"]!;
        Assert.Equal("semantic_text", semantic["type"]!.GetValue<string>());
        Assert.Equal("e5-local", semantic["inference_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_a_basic_license_the_semantic_option_is_left_out_instead_of_breaking_indexing()
    {
        // Licence basic : Elasticsearch refuserait d'indexer dans un champ semantic_text (licence enterprise exigée).
        var (es, fake) = Client("basic");

        await es.EnsureIndexAsync(Ct);

        var (_, path, body) = Assert.Single(fake.Requests, r => r.Method == "PUT");
        Assert.Equal("sonnuage-v2", path);
        Assert.DoesNotContain("semantic_text", body);
        Assert.Equal("sonnuage-v2", es.Index);
        await es.SearchAsync("calme", 10, Ct);
        Assert.DoesNotContain("\"semantic\"", fake.Requests.Last().Body);
    }

    [Fact]
    public async Task Semantic_similarity_reorders_text_matches_but_never_brings_unrelated_names()
    {
        var (es, fake) = Client("trial");
        await es.EnsureIndexAsync(Ct);

        await es.SearchAsync("calme", 10, Ct);

        var lines = fake.Requests.Last(r => r.Path == "_msearch").Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var query = JsonNode.Parse(lines[1])!["query"]!["function_score"]!["query"]!["bool"]!;
        var lexical = query["must"]!["bool"]!["should"]!.AsArray();
        Assert.DoesNotContain(lexical, c => c!["semantic"] is not null);
        Assert.Equal("semantic", Assert.Single(query["should"]!.AsArray())!["semantic"]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tracks_are_indexed_with_their_description_for_the_inference_model()
    {
        var (es, fake) = Client("enterprise");
        await es.EnsureIndexAsync(Ct);
        var track = new SearchDocument("track", Guid.NewGuid(), "Opening", "Artist", "Album", "Ambient", 1994, 0, "Opening Artist Album",
            ["Opening"], Description: "Opening Artist Album ambient calme calm");

        await es.ReplaceAllAsync([track], Ct);

        var source = JsonNode.Parse(fake.Requests.First(r => r.Path == "_bulk").Body.Split('\n')[1])!;
        Assert.Equal("Opening Artist Album ambient calme calm", source["semantic"]!.GetValue<string>());
    }

    [Fact]
    public void A_track_description_tells_its_genre_decade_tags_and_moods()
    {
        var calm = new AudioFeel(Bpm: 80, Relaxed: 0.9, Energy: 0.1, Acoustic: 0.8, Aggressive: 0.05, Instrumental: 0.9);
        var p = TrackProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), true, 200, "Opening", "First Album", "Ambient",
            ["chillout"], calm, 1994);

        var text = AmbianceVocabulary.Describe(p);

        Assert.Contains("ambient", text);
        Assert.Contains("1990s", text);
        Assert.Contains("chillout", text);
        Assert.Contains("calme", text);
        Assert.Contains("calm", text);
        Assert.DoesNotContain("énergique", text);
        Assert.Equal("", AmbianceVocabulary.Describe(TrackProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), true, 200, "x", null, null)));
    }

    /// <summary>Faux Elasticsearch : index absent, licence donnée, réponses vides ; garde chaque requête.</summary>
    private sealed class FakeElasticsearch(string license) : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(string Method, string Path, string Body)> Requests = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://es.test:9200/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, path, body));
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var json = path switch
            {
                "_license" => $$$"""{"license":{"status":"active","type":"{{{license}}}"}}""",
                "_msearch" => """{"responses":[{"hits":{"hits":[]}},{"hits":{"hits":[]}},{"hits":{"hits":[]}}]}""",
                "_bulk" => """{"errors":false,"items":[]}""",
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
