using System.Text.Json.Nodes;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Lecture des réponses InnerTube pour la lecture YouTube directe (Jam) : choix du titre et du flux audio.</summary>
public sealed class YoutubeDirectTests
{
    [Theory]
    [InlineData("3:04", 184)]
    [InlineData("1:02:03", 3723)]
    [InlineData(" 0:59 ", 59)]
    [InlineData("Song", 0)]
    [InlineData("", 0)]
    [InlineData("12", 0)]
    public void Durations_are_read_from_result_text(string text, double expected) =>
        Assert.Equal(expected, YoutubeDirect.ParseDuration(text));

    private static JsonNode Songs(params (string Id, string Duration)[] songs)
    {
        var items = new JsonArray();
        foreach (var (id, duration) in songs)
        {
            JsonObject Column(params string[] texts) => new()
            {
                ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["runs"] = new JsonArray(texts.Select(t => (JsonNode)new JsonObject { ["text"] = t }).ToArray()) },
                },
            };
            items.Add(new JsonObject
            {
                ["musicResponsiveListItemRenderer"] = new JsonObject
                {
                    ["playlistItemData"] = new JsonObject { ["videoId"] = id },
                    ["flexColumns"] = new JsonArray(Column("Title"), Column("Artist", " • ", "Album", " • ", duration)),
                },
            });
        }
        var shelf = new JsonObject { ["musicShelfRenderer"] = new JsonObject { ["contents"] = items } };
        return new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["tabbedSearchResultsRenderer"] = new JsonObject
                {
                    ["tabs"] = new JsonArray(new JsonObject
                    {
                        ["tabRenderer"] = new JsonObject
                        {
                            ["content"] = new JsonObject { ["sectionListRenderer"] = new JsonObject { ["contents"] = new JsonArray(shelf) } },
                        },
                    }),
                },
            },
        };
    }

    [Fact]
    public void The_song_whose_duration_matches_spotify_is_preferred()
    {
        // Le premier résultat est une version live (4:30) ; la version studio (3:04) colle à Spotify (183 s).
        var root = Songs(("live", "4:30"), ("studio", "3:04"));
        Assert.Equal("studio", YoutubeDirect.PickSong(root, 183));
    }

    [Fact]
    public void Without_a_duration_match_the_first_song_wins()
    {
        Assert.Equal("a", YoutubeDirect.PickSong(Songs(("a", "9:00"), ("b", "8:00")), 183));
        Assert.Equal("a", YoutubeDirect.PickSong(Songs(("a", "9:00")), 0));
        Assert.Null(YoutubeDirect.PickSong(JsonNode.Parse("{}"), 183));
    }

    [Fact]
    public void Opus_audio_with_a_direct_url_is_chosen()
    {
        var player = JsonNode.Parse("""
            {"playabilityStatus":{"status":"OK"},"streamingData":{"adaptiveFormats":[
              {"mimeType":"video/mp4","url":"v","bitrate":900000},
              {"mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","url":"aac","bitrate":130000,"contentLength":"3000000","approxDurationMs":"183000"},
              {"mimeType":"audio/webm; codecs=\"opus\"","url":"opus","bitrate":120000,"contentLength":"2900000","approxDurationMs":"183000"},
              {"mimeType":"audio/webm; codecs=\"opus\"","bitrate":160000}]}}
            """);
        var audio = YoutubeDirect.PickAudio("vid", player);
        Assert.NotNull(audio);
        Assert.Equal("opus", audio!.Url);
        Assert.Equal(183, audio.DurationSeconds);
        Assert.Equal(2_900_000, audio.ContentLength);
    }

    [Fact]
    public void A_refused_video_gives_no_audio() =>
        Assert.Null(YoutubeDirect.PickAudio("vid", JsonNode.Parse("""{"playabilityStatus":{"status":"LOGIN_REQUIRED"}}""")));

    [Fact]
    public void A_youtube_track_is_recognised_and_never_collides_with_a_library_id()
    {
        var track = YoutubeTracks.Create("orOgilmiL_4", "NO BATIDÃO", "ZXKAI", 89.7);
        Assert.Equal("orOgilmiL_4", YoutubeTracks.VideoId(track));
        Assert.Equal(track.Id, YoutubeTracks.IdFor("orOgilmiL_4"));
        Assert.NotEqual(track.Id, YoutubeTracks.IdFor("other"));
        Assert.False(YoutubeTracks.IsYoutube(track with { Format = "opus" }));
    }

    [Fact]
    public void The_visitor_id_is_read_from_the_service_worker_data()
    {
        var text = ")]}'\n" + """[["a",null,[[["x",1,2,3,4,5,6,7,8,9,10,11,12,"CgtWaXNpdG9yMTIz"]]]]]""";
        Assert.Equal("CgtWaXNpdG9yMTIz", YoutubeDirect.ParseVisitor(text));
    }

    [Theory]
    [InlineData("<html><body>Before you continue to YouTube</body></html>")]
    [InlineData(")]}'\n[[\"a\",null,[[[\"x\",1]]]]]")]
    [InlineData(")]}'\n[[\"a\",null,[[[0,1,2,3,4,5,6,7,8,9,10,11,12,42]]]]]")]
    [InlineData(")]}'\n[broken")]
    public void An_unexpected_service_worker_reply_gives_no_visitor_id(string text) =>
        Assert.Null(YoutubeDirect.ParseVisitor(text));
}
