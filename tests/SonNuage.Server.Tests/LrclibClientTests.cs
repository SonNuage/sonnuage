using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonNuage.Server.Lyrics;

namespace SonNuage.Server.Tests;

/// <summary>LRCLIB : recherche exacte, puis repli sur la recherche libre au plus près de la durée.</summary>
public sealed class LrclibClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LyricsQuery Query = new("Titre & co", "Artiste", "Album", 200.4);

    [Fact]
    public async Task Exact_match_asks_with_title_artist_album_and_rounded_duration()
    {
        var requests = new List<HttpRequestMessage>();
        var client = Client(requests, _ => Json("""{"syncedLyrics":"[00:01.00]Un","plainLyrics":"Un"}"""));

        var found = await client.FindAsync(Query, Ct);

        Assert.Equal(new LrclibLyrics("[00:01.00]Un", "Un"), found);
        var request = Assert.Single(requests);
        Assert.Equal("http://lrclib.test/api/get?artist_name=Artiste&track_name=Titre%20%26%20co&album_name=Album&duration=200", request.RequestUri!.AbsoluteUri);
        Assert.Contains("SonNuage", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Without_exact_match_the_search_result_closest_in_duration_wins()
    {
        var requests = new List<HttpRequestMessage>();
        var client = Client(requests, r => r.RequestUri!.AbsolutePath == "/api/get"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""
                [
                  {"duration":230,"syncedLyrics":"[00:01.00]Trop long","plainLyrics":"Trop long"},
                  {"duration":202,"syncedLyrics":"[00:01.00]Proche","plainLyrics":"Proche"},
                  {"duration":200,"syncedLyrics":null,"plainLyrics":"Brut"},
                  {"duration":201,"instrumental":true,"syncedLyrics":null,"plainLyrics":null}
                ]
                """));

        var found = await client.FindAsync(Query, Ct);

        Assert.Equal(new LrclibLyrics("[00:01.00]Proche", "Proche"), found);
        Assert.Equal("http://lrclib.test/api/search?track_name=Titre%20%26%20co&artist_name=Artiste", requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Plain_lyrics_are_kept_when_nothing_is_synced()
    {
        var client = Client([], r => r.RequestUri!.AbsolutePath == "/api/get"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""[{"duration":199,"syncedLyrics":"","plainLyrics":"Brut"}]"""));

        Assert.Equal(new LrclibLyrics(null, "Brut"), await client.FindAsync(Query, Ct));
    }

    [Fact]
    public async Task Nothing_close_enough_is_not_found()
    {
        var client = Client([], r => r.RequestUri!.AbsolutePath == "/api/get"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""[{"duration":210,"syncedLyrics":"[00:01.00]Loin","plainLyrics":"Loin"}]"""));

        Assert.Equal(LrclibLyrics.None, await client.FindAsync(Query, Ct));
    }

    [Fact]
    public async Task An_instrumental_exact_match_is_not_found()
    {
        var client = Client([], _ => Json("""{"instrumental":true,"syncedLyrics":null,"plainLyrics":null}"""));

        Assert.Equal(LrclibLyrics.None, await client.FindAsync(Query, Ct));
    }

    [Fact]
    public async Task A_server_error_is_not_a_missing_song()
    {
        var client = Client([], _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindAsync(Query, Ct));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static LrclibClient Client(List<HttpRequestMessage> requests, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Factory(new Handler(r => { requests.Add(r); return respond(r); })),
            Options.Create(new SonNuageOptions { LrclibUrl = "http://lrclib.test/" }), NullLogger<LrclibClient>.Instance);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
