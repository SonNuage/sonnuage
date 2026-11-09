using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Server.Lyrics;

namespace SonNuage.Server.Tests;

/// <summary>Bibliothèque de test avec un fichier .lrc à côté d'un morceau.</summary>
public sealed class LyricsLibraryFactory : SonNuageFactory
{
    public LyricsLibraryFactory()
    {
        File.WriteAllText(Path.Combine(MusicPath, "Test Artist", "First Album", "01 - Opening.lrc"),
            "[ti:Opening]\n[00:00.50]Première ligne\n[00:01.20]Seconde ligne\n");
    }
}

public sealed class LyricsEndpointTests(LyricsLibraryFactory factory) : IClassFixture<LyricsLibraryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Admin, Guid TrackId)> TrackAsync(string query)
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var track = (await admin.GetFromJsonAsync<SearchResultDto>($"/api/search?q={query}", Ct))!.Tracks[0];
        return (admin, track.Id);
    }

    [Fact]
    public async Task A_lrc_file_next_to_the_track_gives_synced_lyrics()
    {
        var (admin, id) = await TrackAsync("opening");

        var lyrics = await admin.GetFromJsonAsync<LyricsDto>($"/api/tracks/{id}/lyrics", Ct);

        Assert.True(lyrics!.Synced);
        Assert.Equal("local", lyrics.Source);
        Assert.Equal([new LyricLineDto(0.5, "Première ligne"), new LyricLineDto(1.2, "Seconde ligne")], lyrics.Lines);
    }

    [Fact]
    public async Task Without_lyrics_anywhere_the_answer_is_not_found()
    {
        var (admin, id) = await TrackAsync("encore");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/tracks/{id}/lyrics", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/tracks/{Guid.NewGuid()}/lyrics", Ct)).StatusCode);
    }

    [Fact]
    public async Task Lyrics_require_authentication()
    {
        var (_, id) = await TrackAsync("opening");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/tracks/{id}/lyrics", Ct)).StatusCode);
    }
}

/// <summary>Cache des paroles de LRCLIB, dans le dossier de données.</summary>
public sealed class LyricsCacheTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LyricsQuery Query = new("Titre", "Artiste", "Album", 180);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnuage-lyrics-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly List<Uri> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private string AudioPath => Path.Combine(_root, "music", "titre.flac");

    private LyricsService Service() => new(
        new LrclibClient(new Factory(new Handler(r => { _requests.Add(r.RequestUri!); return _respond(r); })),
            Options.Create(new SonNuageOptions { LrclibUrl = "http://lrclib.test" }), NullLogger<LrclibClient>.Instance),
        Options.Create(new SonNuageOptions { DataPath = Path.Combine(_root, "data") }), _clock, NullLogger<LyricsService>.Instance);

    [Fact]
    public async Task Lyrics_found_on_lrclib_are_fetched_once()
    {
        _respond = _ => Json("""{"syncedLyrics":"[00:01.00]Un","plainLyrics":"Un"}""");
        var id = Guid.NewGuid();

        var first = await Service().FindAsync(id, AudioPath, Query, Ct);
        var second = await Service().FindAsync(id, AudioPath, Query, Ct);

        Assert.Equal("lrclib", first!.Source);
        Assert.True(first.Synced);
        Assert.Equal(first.Lines, second!.Lines);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Plain_lyrics_from_lrclib_are_not_synced()
    {
        _respond = _ => Json("""{"syncedLyrics":null,"plainLyrics":"Un\nDeux"}""");

        var lyrics = await Service().FindAsync(Guid.NewGuid(), AudioPath, Query, Ct);

        Assert.False(lyrics!.Synced);
        Assert.Equal(["Un", "Deux"], lyrics.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task Missing_lyrics_are_asked_again_only_after_a_week()
    {
        var id = Guid.NewGuid();

        Assert.Null(await Service().FindAsync(id, AudioPath, Query, Ct));
        var asked = _requests.Count;
        Assert.True(asked > 0);
        _clock.Now += TimeSpan.FromDays(6);
        Assert.Null(await Service().FindAsync(id, AudioPath, Query, Ct));
        Assert.Equal(asked, _requests.Count);

        _clock.Now += TimeSpan.FromDays(2);
        _respond = _ => Json("""{"syncedLyrics":"[00:01.00]Enfin","plainLyrics":"Enfin"}""");
        Assert.Equal("Enfin", (await Service().FindAsync(id, AudioPath, Query, Ct))!.Lines[0].Text);
    }

    [Fact]
    public async Task An_unreachable_lrclib_is_asked_again_next_time()
    {
        var id = Guid.NewGuid();
        _respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Null(await Service().FindAsync(id, AudioPath, Query, Ct));

        _respond = _ => Json("""{"syncedLyrics":"[00:01.00]Un","plainLyrics":"Un"}""");
        Assert.NotNull(await Service().FindAsync(id, AudioPath, Query, Ct));
    }

    [Fact]
    public async Task A_local_lrc_file_wins_over_lrclib()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AudioPath)!);
        await File.WriteAllTextAsync(Path.ChangeExtension(AudioPath, ".lrc"), "[00:02.00]Locale", Ct);
        _respond = _ => Json("""{"syncedLyrics":"[00:01.00]Distante","plainLyrics":"Distante"}""");

        var lyrics = await Service().FindAsync(Guid.NewGuid(), AudioPath, Query, Ct);

        Assert.Equal("local", lyrics!.Source);
        Assert.Equal("Locale", Assert.Single(lyrics.Lines).Text);
        Assert.Empty(_requests);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
