using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Deux onglets reliés : un seul joue, l'autre le pilote, et la lecture se récupère quand il part.</summary>
public class LinkedPlayerTests
{
    private static readonly TrackDto[] Tracks = [Track("A"), Track("B"), Track("C")];

    [Fact]
    public async Task Only_one_tab_plays_and_the_other_drives_it()
    {
        var (a, localA, b, localB, _) = await TwoTabsAsync();

        await a.PlayAsync(Tracks, 1);
        Assert.True(localA.State.IsPlaying);
        Assert.True(b.IsRemote);
        Assert.Equal("B", b.State.Current?.Title);
        Assert.Equal(3, b.State.Queue.Count);

        // Les commandes de l'onglet B s'exécutent dans l'onglet A. B ne lit rien lui-même.
        await b.NextAsync();
        await b.SeekAsync(42);
        Assert.Equal("C", localA.State.Current?.Title);
        Assert.Equal(42, localA.State.PositionSeconds);
        Assert.Equal("C", b.State.Current?.Title);
        Assert.False(localB.State.IsPlaying);
        Assert.Empty(localB.State.Queue);

        // Choisir un album dans B le fait jouer dans A.
        await b.PlayAsync([Track("X")], 0);
        Assert.Equal("X", localA.State.Current?.Title);
        Assert.False(localB.State.IsPlaying);
    }

    [Fact]
    public async Task A_station_continues_only_on_the_tab_that_plays_it()
    {
        var (a, localA, b, _, _) = await TwoTabsAsync();

        await a.PlayStationAsync("chill", Tracks);
        Assert.Equal("chill", localA.Station);

        await b.PlayStationAsync("party", Tracks);
        Assert.True(b.IsRemote);
        Assert.Null(localA.Station);
    }

    [Fact]
    public async Task Play_here_moves_playback_to_this_tab()
    {
        var (a, localA, b, localB, _) = await TwoTabsAsync();
        await a.PlayAsync(Tracks, 0);
        await a.SeekAsync(10);

        await ((IConnectPlayer)b).PlayHereAsync();

        Assert.True(localB.State.IsPlaying);
        Assert.Equal(10, localB.State.PositionSeconds);
        Assert.False(localA.State.IsPlaying);
        Assert.True(a.IsRemote);
        Assert.False(b.IsRemote);
    }

    [Fact]
    public async Task Closing_the_playing_tab_hands_the_queue_over_paused()
    {
        var (a, _, b, localB, bus) = await TwoTabsAsync();
        await a.PlayAsync(Tracks, 2);

        await bus.SendAsync(new PlaybackMessage("bye", "tab-a"));

        Assert.False(b.IsRemote);
        Assert.Equal("C", localB.State.Current?.Title);
        Assert.False(localB.State.IsPlaying);
    }

    [Fact]
    public async Task A_new_tab_finds_the_playing_one_instead_of_restoring_its_own_queue()
    {
        var bus = new Bus();
        var localA = new FakePlayer();
        var a = new LinkedPlayer(localA, localA, bus.Join("tab-a"));
        await a.RestoreAsync();
        await a.PlayAsync(Tracks, 0);

        var localB = new FakePlayer();
        var b = new LinkedPlayer(localB, localB, bus.Join("tab-b"));
        await b.RestoreAsync();

        Assert.True(b.IsRemote);
        Assert.False(localB.Restored);
        Assert.Equal(3, b.State.Queue.Count);
    }

    [Fact]
    public void Messages_survive_the_json_trip_through_the_browser_channel()
    {
        var state = PlayerState.Empty with { Queue = Tracks, CurrentIndex = 1, IsPlaying = true, PositionSeconds = 12.5, Repeat = RepeatMode.All };
        var json = System.Text.Json.JsonSerializer.Serialize(new PlaybackMessage("state", "tab-a", State: state, HasQueue: true), JsonDefaults.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<PlaybackMessage>(json, JsonDefaults.Options)!;
        Assert.Equal("B", back.State!.Current?.Title);
        Assert.Equal(RepeatMode.All, back.State.Repeat);
        Assert.Equal(12.5, back.State.PositionSeconds);

        var bye = System.Text.Json.JsonSerializer.Deserialize<PlaybackMessage>("""{"type":"bye","from":"tab-a"}""", JsonDefaults.Options)!;
        Assert.Equal(("bye", "tab-a"), (bye.Type, bye.From));
    }

    [Fact]
    public async Task The_device_that_plays_is_shown_by_its_name()
    {
        var (a, _, b, _, _) = await TwoTabsAsync();

        await a.PlayAsync(Tracks, 0);

        Assert.Equal("Name of tab-a", ((IConnectPlayer)b).RemoteName);
        Assert.Null(((IConnectPlayer)a).RemoteName);
    }

    [Fact]
    public async Task After_a_reconnection_the_playing_device_sends_its_whole_queue_again()
    {
        var (a, _, b, _, bus) = await TwoTabsAsync();
        await a.PlayAsync(Tracks, 0);
        // B a été coupé du relais : il a reçu le « bye » de A et repris la file en pause.
        await bus.SendAsync(new PlaybackMessage("bye", "tab-a"));
        Assert.False(b.IsRemote);

        await bus.ReconnectAsync("tab-a");

        Assert.True(b.IsRemote);
        Assert.Equal(3, b.State.Queue.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_crossfade_setting_follows_what_the_local_player_can_do(bool supported)
    {
        // Sur le web, Réglages ne montre le fondu enchaîné que si le lecteur relié le gère.
        var local = new FakePlayer { SupportsCrossfade = supported };
        Assert.Equal(supported, new LinkedPlayer(local, local, new Bus().Join("tab")).SupportsCrossfade);
    }

    private static async Task<(LinkedPlayer A, FakePlayer LocalA, LinkedPlayer B, FakePlayer LocalB, Bus Bus)> TwoTabsAsync()
    {
        var bus = new Bus();
        var localA = new FakePlayer();
        var localB = new FakePlayer();
        var a = new LinkedPlayer(localA, localA, bus.Join("tab-a"));
        var b = new LinkedPlayer(localB, localB, bus.Join("tab-b"));
        await a.RestoreAsync();
        await b.RestoreAsync();
        return (a, localA, b, localB, bus);
    }

    private static TrackDto Track(string title) => new(
        Guid.NewGuid(), title, Guid.NewGuid(), "Artiste", Guid.NewGuid(), "Album", 1, 1, 200, "mp3", false, 320, false, false);

    /// <summary>BroadcastChannel en mémoire : chaque message part vers tous les autres onglets, aussitôt.</summary>
    private sealed class Bus
    {
        private readonly List<Link> _links = [];

        public Link Join(string id)
        {
            var link = new Link(id, this);
            _links.Add(link);
            return link;
        }

        /// <summary>Comme le relais du serveur : le nom de l'expéditeur est ajouté au message.</summary>
        public async Task SendAsync(PlaybackMessage m)
        {
            m = m with { FromName = "Name of " + m.From };
            foreach (var l in _links.ToList())
            {
                if (l.DeviceId == m.From || m.To is { } to && to != l.DeviceId || l.OnMessage is null) continue;
                await l.OnMessage(m);
            }
        }

        public async Task ReconnectAsync(string id)
        {
            if (_links.Single(l => l.DeviceId == id).OnConnected is { } connected) await connected();
        }
    }

    private sealed class Link(string id, Bus bus) : IPlaybackLink
    {
        public string DeviceId => id;
        public Func<PlaybackMessage, Task>? OnMessage { get; set; }
        public Func<Task>? OnConnected { get; set; }
        public Task StartAsync() => Task.CompletedTask;
        public Task SendAsync(PlaybackMessage message) => bus.SendAsync(message);
    }
}
