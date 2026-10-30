using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;
using System.Text.Json.Nodes;
using SonNuage.Server.Admin;
using SonNuage.Server.Api;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

public class YoutubeTitleTests
{
    [Theory]
    [InlineData("Daft Punk - Get Lucky (Official Video) ft. Pharrell Williams, Nile Rodgers", null, "Daft Punk", "Get Lucky")]
    [InlineData("Stromae - Alors On Danse [Clip Officiel]", null, "Stromae", "Alors On Danse")]
    [InlineData("Get Lucky", "Daft Punk - Topic", "Daft Punk", "Get Lucky")]
    [InlineData("Bad Romance (Official Music Video)", "LadyGagaVEVO", "LadyGaga", "Bad Romance")]
    [InlineData("Angèle \"Balance ton quoi\"", null, "Angèle", "Balance ton quoi")]
    [InlineData("AC/DC \u2013 Thunderstruck (Official Video) [HD]", "AC/DC", "AC/DC", "Thunderstruck")]
    [InlineData("Some song without artist", null, null, "Some song without artist")]
    [InlineData("Kevin MacLeod: Monkeys Spinning Monkeys", "Kevin MacLeod Archive", "Kevin MacLeod", "Monkeys Spinning Monkeys")]
    public void Splits_artist_and_title(string video, string? channel, string? artist, string title)
    {
        var parsed = TitleParser.Parse(video, channel);
        Assert.Equal(artist, parsed.Artist);
        Assert.Equal(title, parsed.Title);
    }

    [Fact]
    public void Extracts_featuring()
    {
        Assert.Equal("Pharrell Williams", TitleParser.Parse("Daft Punk - Get Lucky (feat. Pharrell Williams)", null).Featuring);
    }

    [Theory]
    [InlineData("Get Lucky", "get lucky", 1.0)]
    [InlineData("Au-delà du délire", "Au‐Delà du Délire", 1.0)]
    [InlineData("Get Lucky (Radio Edit)", "Get Lucky", 1.0)]
    public void Similarity_ignores_case_accents_and_suffixes(string a, string b, double expected)
    {
        Assert.Equal(expected, TitleParser.Similarity(a, b), 3);
    }

    [Fact]
    public void Similarity_separates_different_titles()
    {
        Assert.True(TitleParser.Similarity("Get Lucky", "Lose Yourself to Dance") < 0.3);
        Assert.True(TitleParser.Similarity("Thunderstruck", "Thunderstuck") > 0.8);
    }
}

public class YoutubeMetadataTests
{
    private const string Recording = """
        {
          "id": "rec-1", "score": 100, "title": "Get Lucky", "length": 369000,
          "artist-credit": [
            { "name": "Daft Punk", "joinphrase": " feat. ", "artist": { "id": "a1", "name": "Daft Punk" } },
            { "name": "Pharrell Williams", "artist": { "id": "a2", "name": "Pharrell Williams" } }
          ],
          "first-release-date": "2013-04-19",
          "tags": [ { "count": 1, "name": "funk" }, { "count": 5, "name": "disco" } ],
          "releases": [
            { "id": "comp", "title": "NOW That's What I Call Music! 85", "status": "Official", "date": "2013-07-22",
              "artist-credit": [ { "name": "Various Artists" } ],
              "release-group": { "id": "rg-comp", "primary-type": "Album", "secondary-types": ["Compilation"] },
              "media": [ { "position": 1, "track": [ { "number": "1", "position": 1 } ] } ] },
            { "id": "single", "title": "Get Lucky", "status": "Official", "date": "2013-04-19",
              "release-group": { "id": "rg-single", "primary-type": "Single" },
              "media": [ { "position": 1, "track": [ { "number": "1", "position": 1 } ] } ] },
            { "id": "ram", "title": "Random Access Memories", "status": "Official", "date": "2013-05-17",
              "release-group": { "id": "rg-ram", "primary-type": "Album" },
              "media": [ { "position": 1, "track": [ { "number": "8", "position": 8 } ] } ] }
          ]
        }
        """;

    private static MbRecording Parse() => MusicBrainzClient.ParseRecording(JsonNode.Parse(Recording)!.AsObject());

    [Fact]
    public void Parses_recording_with_credits_and_releases()
    {
        var rec = Parse();
        Assert.Equal("Daft Punk feat. Pharrell Williams", rec.ArtistCredit);
        Assert.Equal(369, rec.LengthSeconds);
        Assert.Equal("disco", rec.TopTag);
        Assert.Equal(3, rec.Releases.Count);
        Assert.Equal("Various Artists", rec.Releases[0].ArtistCredit);
    }

    [Fact]
    public void Prefers_the_studio_album_over_singles_and_compilations()
    {
        var c = MetadataResolver.ToCandidate(Parse(), null, 0.9, "musicbrainz");
        Assert.Equal("Random Access Memories", c.Album);
        Assert.Equal("Daft Punk feat. Pharrell Williams", c.AlbumArtist);
        Assert.Equal(8, c.TrackNumber);
        Assert.Equal(2013, c.Year);
        Assert.Equal("Disco", c.Genre);
        Assert.Equal("rg-ram", c.ReleaseGroupId);
    }

    [Fact]
    public void Album_hint_wins()
    {
        Assert.Equal("single", MetadataResolver.PickRelease(Parse().Releases, "Get Lucky")!.Id);
    }

    [Fact]
    public void Text_score_rewards_matching_title_artist_and_duration()
    {
        var rec = Parse();
        var good = MetadataResolver.TextScore(rec, "Get Lucky", "Daft Punk", 368);
        var wrongDuration = MetadataResolver.TextScore(rec, "Get Lucky", "Daft Punk", 248);
        var wrongArtist = MetadataResolver.TextScore(rec, "Get Lucky", "Muse", 368);
        Assert.True(good > 0.9, $"good = {good}");
        Assert.True(wrongDuration < good && wrongArtist < good);
        Assert.True(wrongArtist < 0.8, $"wrongArtist = {wrongArtist}");
    }

    [Fact]
    public void Youtube_music_fields_make_a_confident_candidate()
    {
        var info = JsonNode.Parse("""{ "title": "Get Lucky", "track": "Get Lucky", "artists": ["Daft Punk", "Pharrell Williams"], "album": "Random Access Memories", "release_year": 2013 }""")!.AsObject();
        var c = MetadataResolver.FromYoutubeMusic(info, null)!;
        Assert.Equal("Daft Punk, Pharrell Williams", c.Artist);
        Assert.Equal("Daft Punk", c.AlbumArtist);
        Assert.Equal("Random Access Memories", c.Album);
        Assert.Equal(2013, c.Year);
        Assert.True(c.Score >= 0.8);
        Assert.Null(MetadataResolver.FromYoutubeMusic(JsonNode.Parse("""{ "title": "x" }""")!.AsObject(), null));
        var fragment = JsonNode.Parse("""{ "track": "Monkeys Spinning Monkeys", "artists": ["Kevin MacLeod", "Kevin"] }""")!.AsObject();
        Assert.Equal("Kevin MacLeod", MetadataResolver.FromYoutubeMusic(fragment, null)!.Artist);
    }
}

public class YoutubeMiscTests
{
    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you’re not a bot.", YtDlpFailure.Throttled)]
    [InlineData("ERROR: unable to download video data: HTTP Error 429: Too Many Requests", YtDlpFailure.Throttled)]
    [InlineData("ERROR: [youtube] abc: Video unavailable. This video is private", YtDlpFailure.Unavailable)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", YtDlpFailure.Unavailable)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users.", YtDlpFailure.NeedsAccount)]
    [InlineData("ERROR: [youtube] abc: Join this channel to get access to members-only content like this video", YtDlpFailure.NeedsAccount)]
    [InlineData("ERROR: [youtube] abc: Requested format is not available. Use --list-formats for a list of available formats", YtDlpFailure.FormatUnavailable)]
    [InlineData("ERROR: Postprocessing: something odd", YtDlpFailure.Other)]
    public void Classifies_yt_dlp_errors(string error, YtDlpFailure expected)
    {
        Assert.Equal(expected, YtDlp.Classify(error));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abc", "https://music.youtube.com/playlist?list=OLAK5uy_abc")]
    [InlineData("https://www.youtube.com/@DaftPunk", "https://www.youtube.com/@DaftPunk")]
    [InlineData("https://www.youtube.com/channel/UC123/", "https://www.youtube.com/channel/UC123")]
    [InlineData("http://www.youtube.com/@DaftPunk/playlists", "https://www.youtube.com/@DaftPunk/playlists")]
    [InlineData("https://evil.example.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("https://youtube.com.evil.org/x", null)]
    [InlineData("not a url", null)]
    public void Accepts_only_youtube_urls(string input, string? expected)
    {
        Assert.Equal(expected, YoutubeSyncService.NormalizeUrl(input));
    }

    // Racine de chaîne : on y lit l'onglet Sorties (albums, singles) plutôt que les seules vidéos.
    [Theory]
    [InlineData("https://www.youtube.com/@orelsan", true)]
    [InlineData("https://music.youtube.com/@orelsan", true)]
    [InlineData("https://www.youtube.com/channel/UC123", true)]
    [InlineData("https://www.youtube.com/@orelsan/videos", false)]
    [InlineData("https://www.youtube.com/@orelsan/releases", false)]
    [InlineData("https://www.youtube.com/playlist?list=OLAK5uy_abc", false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    public void Detects_channel_roots(string url, bool expected)
    {
        Assert.Equal(expected, YoutubeSyncService.IsChannelRoot(url));
    }

    [Theory]
    [InlineData("140", true)]
    [InlineData("bestaudio[acodec=opus]/bestaudio[ext=m4a]/18", true)]
    [InlineData("251/140 --exec rm", false)]
    [InlineData("", false)]
    [InlineData("a;b", false)]
    public void Validates_format_selectors(string format, bool valid)
    {
        Assert.Equal(valid, YtDlp.IsValidFormat(format));
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("a-b_c123456", true)]
    [InlineData("short", false)]
    [InlineData("../../etc/pw", false)]
    public void Validates_video_ids(string id, bool valid)
    {
        Assert.Equal(valid, YoutubeImporter.IsVideoId(id));
    }

    [Fact]
    public void Folder_names_keep_slashes_readable()
    {
        Assert.Equal("AC_DC", UploadService.SafeSegment("AC/DC"));
        Assert.Equal("DC", UploadService.SafeFileName("AC/DC"));
    }

    [Theory]
    [InlineData("day", 24)]
    [InlineData("week", 7)]
    [InlineData("month", 30)]
    [InlineData("year", 12)]
    [InlineData(null, 30)]
    public void Dashboard_buckets(string? range, int count)
    {
        var now = new DateTimeOffset(2026, 10, 29, 17, 42, 0, TimeSpan.FromHours(1));
        var (_, starts) = DashboardEndpoints.Buckets(range, now, null);
        Assert.Equal(count, starts.Count);
        Assert.True(starts[^1] <= now && starts.SequenceEqual(starts.Order()));
    }

    [Fact]
    public void Dashboard_all_range_starts_at_the_first_import()
    {
        var now = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var (_, months) = DashboardEndpoints.Buckets("all", now, new DateTimeOffset(2025, 11, 3, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero), months[0]);
        Assert.Equal(12, months.Count);
        var (_, years) = DashboardEndpoints.Buckets("all", now, new DateTimeOffset(2001, 5, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(26, years.Count);
    }
}

public sealed class YoutubePipelineTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_finished_download_is_found_again_after_a_restart_but_not_a_partial_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonnuage-ytdl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "abcdefghijk.webm"), "audio", Ct);
            Assert.Null(await YtDlp.ReadDownloadAsync(dir, "abcdefghijk", Ct));

            await File.WriteAllTextAsync(Path.Combine(dir, "abcdefghijk.info.json"), """{"title":"Morceau","duration":12}""", Ct);
            var download = await YtDlp.ReadDownloadAsync(dir, "abcdefghijk", Ct);
            Assert.NotNull(download);
            Assert.EndsWith(".webm", download.AudioPath);
            Assert.Equal("Morceau", YtDlp.Str(download.Info, "title"));

            Assert.Null(await YtDlp.ReadDownloadAsync(Path.Combine(dir, "absent"), "abcdefghijk", Ct));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(7, HttpStatusCode.BadRequest)]
    [InlineData(4, HttpStatusCode.OK)]
    public async Task Parallel_downloads_setting_is_bounded(int parallel, HttpStatusCode expected)
    {
        var admin = await factory.AdminClientAsync();
        var response = await admin.PutAsJsonAsync("/api/admin/youtube/settings", YoutubeSettingsStore.Defaults with { ParallelDownloads = parallel }, Ct);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
            Assert.Equal(parallel, (await response.Content.ReadFromJsonAsync<YoutubeSettingsDto>(Ct))!.ParallelDownloads);
    }
}
