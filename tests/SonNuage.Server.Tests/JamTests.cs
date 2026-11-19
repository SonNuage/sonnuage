using System.Text;
using SonNuage.Contracts;
using SonNuage.Server.Jam;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Logique pure de « Rejoindre un Jam » : lecture d'un lien de partage et de l'état de lecture de l'hôte.</summary>
public sealed class JamTests
{
    [Theory]
    [InlineData("https://open.spotify.com/socialsession/3t2Yn4EKMk4APNPw22vlZg?si=abc", "3t2Yn4EKMk4APNPw22vlZg")]
    [InlineData("open.spotify.com/socialsession/AbC123dEf456GhI789jk", "AbC123dEf456GhI789jk")]
    [InlineData("3t2Yn4EKMk4APNPw22vlZg", "3t2Yn4EKMk4APNPw22vlZg")]
    [InlineData("spotify:socialsession:3t2Yn4EKMk4APNPw22vlZg", "3t2Yn4EKMk4APNPw22vlZg")]
    public void A_direct_link_or_token_yields_the_session_id(string link, string expected) =>
        Assert.Equal(expected, SpotifyLink.FromText(link));

    [Theory]
    [InlineData("https://spotify.link/PEogbB1787b")]
    [InlineData("hello")]
    [InlineData("")]
    public void A_short_link_or_junk_is_not_read_without_following_it(string link) =>
        Assert.Null(SpotifyLink.FromText(link));

    [Fact]
    public void Join_request_detects_the_service_from_the_link()
    {
        Assert.Equal(SonNuage.Contracts.Jam.Spotify, SonNuage.Contracts.JamJoinRequest.Service("https://spotify.link/x"));
        Assert.Equal(SonNuage.Contracts.Jam.Spotify, SonNuage.Contracts.JamJoinRequest.Service("https://open.spotify.com/socialsession/x"));
        Assert.Equal(SonNuage.Contracts.Jam.Deezer, SonNuage.Contracts.JamJoinRequest.Service("https://deezer.com/jam/x"));
        Assert.Null(SonNuage.Contracts.JamJoinRequest.Service("https://example.com/x"));
    }

    [Fact]
    public void The_cluster_response_gives_the_host_track_and_position()
    {
        var json = """
        {"active_device_id":"d1","server_timestamp_ms":"1794940457100","player_state":{
          "track":{"uri":"spotify:track:0bq82vecdWk6Ih5u76EZwO","metadata":{"title":"Pizza in the Cosmos"}},
          "position_as_of_timestamp":"2565","timestamp":"1794939287197","is_paused":true,"duration":"218058"}}
        """;
        var state = ClusterState.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Equal("0bq82vecdWk6Ih5u76EZwO", state!.TrackId);
        Assert.Equal("Pizza in the Cosmos", state.Title);
        Assert.True(state.IsPaused);
        Assert.Equal(218058, state.DurationMs);
    }

    [Fact]
    public void A_dealer_cluster_update_is_decoded_from_protobuf()
    {
        // Les push dealer sont du protobuf ClusterUpdate, pas du JSON (comme go-librespot).
        // ProvidedTrack{ uri=1 } ; PlayerState{ timestamp=1, track=7, position=10, duration=11, is_paused=13, next_tracks=20 }
        // Cluster{ player_state=3 } ; ClusterUpdate{ cluster=1 }
        var track = Pb.Msg(Pb.Str(1, "spotify:track:abc"));
        var next = Pb.Msg(Pb.Str(1, "spotify:track:def"));
        var playerState = Pb.Concat(Pb.Varint(1, 2000), Pb.Len(7, track), Pb.Varint(10, 1000), Pb.Varint(11, 200000), Pb.Varint(13, 0), Pb.Len(20, next));
        var cluster = Pb.Len(3, playerState);
        var clusterUpdate = Pb.Len(1, cluster);

        var state = ClusterState.ParseClusterUpdate(clusterUpdate);
        Assert.NotNull(state);
        Assert.Equal("abc", state!.TrackId);
        Assert.Equal(1000, state.PositionMs);
        Assert.Equal(2000, state.TimestampUnixMs);
        Assert.False(state.IsPaused);
        Assert.Equal(["spotify:track:def"], state.NextTrackUris);
    }

    /// <summary>Encodeur protobuf minimal pour fabriquer des messages de test.</summary>
    private static class Pb
    {
        public static byte[] Varint(int field, long value)
        {
            var tag = EncodeVarint(((long)field << 3) | 0);
            return [.. tag, .. EncodeVarint(value)];
        }
        public static byte[] Str(int field, string s) => Len(field, Encoding.UTF8.GetBytes(s));
        public static byte[] Len(int field, byte[] bytes)
        {
            var tag = EncodeVarint(((long)field << 3) | 2);
            return [.. tag, .. EncodeVarint(bytes.Length), .. bytes];
        }
        public static byte[] Msg(params byte[] field) => field;
        public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        private static byte[] EncodeVarint(long value)
        {
            var bytes = new List<byte>();
            var v = (ulong)value;
            do { var b = (byte)(v & 0x7f); v >>= 7; if (v != 0) b |= 0x80; bytes.Add(b); } while (v != 0);
            return [.. bytes];
        }
    }

    [Fact]
    public void A_dealer_json_cluster_push_gives_the_host_track_and_queue()
    {
        // Appareil enregistré en JSON → le dealer pousse du JSON {"cluster":{"player_state":…}}.
        var json = """
        {"cluster":{"active_device_id":"social-connect-x","player_state":{"timestamp":"1794953916659",
          "track":{"uri":"spotify:track:2PYXOG6GdxF9sUIH2NfOAD"},"position_as_of_timestamp":"0","duration":"244320",
          "is_paused":false,"next_tracks":[{"uri":"spotify:track:6NgYIETQ8U72CVfkzYhK30"},{"uri":"spotify:delimiter"}]}},
         "update_reason":"DEVICE_STATE_CHANGED"}
        """;
        var state = ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Equal("2PYXOG6GdxF9sUIH2NfOAD", state!.TrackId);
        Assert.Equal(244320, state.DurationMs);
        Assert.False(state.IsPaused);
        Assert.Equal(["spotify:track:6NgYIETQ8U72CVfkzYhK30"], state.NextTrackUris);
    }

    [Fact]
    public void The_host_position_compensates_the_spotify_clock_offset()
    {
        // Notre horloge a 2 s d'avance sur celle de Spotify : sans compensation on croirait l'hôte 2 s plus loin.
        var at = DateTimeOffset.UtcNow;
        var state = new ClusterState("spotify:track:x", "X", 10_000, at.ToUnixTimeMilliseconds() - 2_000, IsPaused: false, 200_000);
        Assert.Equal(14, state.PositionSecondsAt(at.AddSeconds(2), spotifyClockOffsetMs: 0), precision: 1);
        Assert.Equal(12, state.PositionSecondsAt(at.AddSeconds(2), spotifyClockOffsetMs: 2_000), precision: 1);
    }

    [Theory]
    [InlineData("""{"session":{"session_id":"s"},"reason":"SESSION_DELETED"}""", true)]
    [InlineData("""{"reason":"YOU_WERE_KICKED"}""", true)]
    [InlineData("""{"update":{"reason":"YOU_LEFT"}}""", true)]
    [InlineData("""{"reason":"YOU_WERE_REMOVED_UNSUPPORTED_CONTENT"}""", true)]
    [InlineData("""{"reason":"USER_JOINED"}""", false)]
    [InlineData("""{"reason":"SESSION_MEMBER_PAUSE_CHANGED"}""", false)]
    [InlineData("""not json""", false)]
    public void A_session_update_tells_whether_the_jam_is_over(string payload, bool over) =>
        Assert.Equal(over, SessionUpdate.EndsJam(Encoding.UTF8.GetBytes(payload)));

    [Theory]
    [InlineData("http://127.0.0.1:2375/containers/json")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://example.com/socialsession")]
    [InlineData("http://spotify.link/abc")]
    public async Task A_link_outside_spotify_is_never_fetched(string link)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        Assert.Null(await SpotifyLink.ResolveAsync(new HttpClient(handler), link, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_short_link_redirecting_outside_spotify_is_not_followed()
    {
        var handler = new RecordingHandler(req => req.RequestUri!.Host == "spotify.link"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("http://10.0.0.1/admin") } }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        Assert.Null(await SpotifyLink.ResolveAsync(new HttpClient(handler, disposeHandler: true), "https://spotify.link/PEogbB1787b", CancellationToken.None));
        Assert.Equal(["spotify.link"], handler.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task A_short_link_resolves_through_spotify_redirects()
    {
        var handler = new RecordingHandler(req => req.RequestUri!.Host == "spotify.link"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://open.spotify.com/socialsession/3t2Yn4EKMk4APNPw22vlZg?si=x") } }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        Assert.Equal("3t2Yn4EKMk4APNPw22vlZg", await SpotifyLink.ResolveAsync(new HttpClient(handler), "spotify.link/PEogbB1787b", CancellationToken.None));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var res = respond(request);
            res.RequestMessage = request;
            return Task.FromResult(res);
        }
    }

    [Fact]
    public void An_import_keeps_a_well_scored_match_over_a_weak_one_with_the_right_duration()
    {
        RankedEntry Ranked(string id, int score, double duration) => new(new YtEntry(id, id, null, duration, null), score);
        var good = Ranked("good", 80, 210);
        var exactButWeak = Ranked("weak", 30, 200);

        Assert.Equal("good", JamResolver.PickImport([good, exactButWeak], targetSeconds: 200)?.Entry.Id);
        Assert.Equal("exact", JamResolver.PickImport([good, Ranked("exact", 60, 201)], targetSeconds: 200)?.Entry.Id);
        Assert.Null(JamResolver.PickImport([exactButWeak], targetSeconds: 200));
    }

    [Fact]
    public void An_ad_or_episode_from_the_host_is_not_a_track_to_follow()
    {
        var json = """{"player_state":{"track":{"uri":"spotify:ad:123"},"position_as_of_timestamp":"5000","timestamp":"1","duration":"30000"}}""";
        var state = ClusterState.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Null(state!.TrackId);
    }

    [Theory]
    [InlineData("""{"cluster":{"player_state":{"track":{"uri":42},"position_as_of_timestamp":"0"}}}""")]
    [InlineData("""{"cluster":{"player_state":{"track":{"uri":"spotify:track:a","metadata":{"title":7}}}}}""")]
    [InlineData("""{"cluster":{"player_state":{"track":{"uri":"spotify:track:a"},"next_tracks":[1,{"uri":2}]}}}""")]
    public void A_push_with_unexpected_value_types_is_ignored_not_thrown(string json)
    {
        var state = ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json));
        Assert.True(state is null || state.TrackUri == "spotify:track:a");
    }

    [Fact]
    public void A_json_push_carries_the_spotify_server_time()
    {
        var json = """{"cluster":{"server_timestamp_ms":"1794953917038","player_state":{"timestamp":"1794953916659","track":{"uri":"spotify:track:a"},"position_as_of_timestamp":"0"}}}""";
        Assert.Equal(1794953917038, ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json))!.ServerTimestampMs);
    }

    [Fact]
    public void A_state_without_a_track_is_ignored()
    {
        var json = """{"player_state":{"position_as_of_timestamp":"0","is_paused":true}}""";
        Assert.Null(ClusterState.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_paused_position_does_not_drift_but_a_playing_one_advances()
    {
        var at = DateTimeOffset.UtcNow;
        var paused = new ClusterState("spotify:track:x", "X", 30_000, at.ToUnixTimeMilliseconds(), IsPaused: true, 200_000);
        Assert.Equal(30, paused.PositionSecondsAt(at.AddSeconds(10)), precision: 0);

        var playing = paused with { IsPaused = false };
        Assert.Equal(40, playing.PositionSecondsAt(at.AddSeconds(10)), precision: 0);
    }
}
