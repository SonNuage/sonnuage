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

        // Les commandes de l'onglet B s'exécutent dans l'onglet A ; B ne lit rien lui-même.
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

        public async Task SendAsync(PlaybackMessage m)
        {
            foreach (var l in _links.ToList())
            {
                if (l.DeviceId == m.From || m.To is { } to && to != l.DeviceId || l.OnMessage is null) continue;
                await l.OnMessage(m);
            }
        }
    }

    private sealed class Link(string id, Bus bus) : IPlaybackLink
    {
        public string DeviceId => id;
        public Func<PlaybackMessage, Task>? OnMessage { get; set; }
        public Task StartAsync() => Task.CompletedTask;
        public Task SendAsync(PlaybackMessage message) => bus.SendAsync(message);
    }

    /// <summary>Lecteur local minimal : la file et l'état, sans audio.</summary>
    private sealed class FakePlayer : IPlayer, IRestorablePlayer, IAdoptablePlayer
    {
        private List<TrackDto> _queue = [];

        public PlayerState State { get; private set; } = PlayerState.Empty;
        public bool Restored { get; private set; }
        public event Action? StateChanged;

        private Task Set(PlayerState s)
        {
            State = s;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task RestoreAsync()
        {
            Restored = true;
            return Task.CompletedTask;
        }

        public Task AdoptAsync(PlayerState state, bool autoplay)
        {
            _queue = state.Queue.ToList();
            return Set(State with { Queue = _queue, CurrentIndex = state.CurrentIndex, PositionSeconds = state.PositionSeconds, IsPlaying = autoplay });
        }

        public Task ReleaseAsync() => Set(State with { IsPlaying = false });

        public Task PlayAsync(IReadOnlyList<TrackDto> tracks, int startIndex = 0, bool? shuffle = null)
        {
            _queue = tracks.ToList();
            return Set(State with { Queue = _queue, CurrentIndex = startIndex, IsPlaying = true, PositionSeconds = 0 });
        }

        public Task TogglePlayAsync() => Set(State with { IsPlaying = !State.IsPlaying });
        public Task NextAsync() => Set(State with { CurrentIndex = Math.Min(State.CurrentIndex + 1, _queue.Count - 1), PositionSeconds = 0 });
        public Task PreviousAsync() => Set(State with { CurrentIndex = Math.Max(State.CurrentIndex - 1, 0), PositionSeconds = 0 });
        public Task SeekAsync(double seconds) => Set(State with { PositionSeconds = seconds });
        public Task JumpToAsync(int queueIndex) => Set(State with { CurrentIndex = queueIndex });
        public Task SetShuffleAsync(bool shuffle) => Set(State with { Shuffle = shuffle });
        public Task SetRepeatAsync(RepeatMode mode) => Set(State with { Repeat = mode });
        public Task SetVolumeAsync(double volume) => Set(State with { Volume = volume });

        public Task EnqueueAsync(IReadOnlyList<TrackDto> tracks, bool playNext)
        {
            _queue = [.. _queue, .. tracks];
            return Set(State with { Queue = _queue });
        }

        public Task RemoveAtAsync(int queueIndex)
        {
            _queue = _queue.Where((_, i) => i != queueIndex).ToList();
            return Set(State with { Queue = _queue });
        }

        public Task ReloadSettingsAsync() => Task.CompletedTask;
        public void UpdateTrack(TrackDto track) { }
    }
}
