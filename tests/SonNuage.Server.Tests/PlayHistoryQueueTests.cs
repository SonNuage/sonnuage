using System.Net;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Côté appareil : les écoutes partent au serveur, ou attendent dans une file locale tant qu'on est hors ligne.</summary>
public class PlayHistoryQueueTests
{
    private static readonly Uri Server = new("https://music.example.org/");
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static TrackDto Track(double duration = 200) =>
        new(Guid.NewGuid(), "Titre", Guid.NewGuid(), "Artiste", Guid.NewGuid(), "Album", 1, 1, duration, "mp3", false, null, false, false);

    private static (MemoryStore Store, AccountStore Accounts, FakeApi Api, PlayHistory History) Create(bool online = true)
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: false);
        var api = new FakeApi(accounts) { Online = online };
        return (store, accounts, api, new PlayHistory(store, accounts, api, new Platform()));
    }

    [Fact]
    public async Task A_play_is_sent_with_its_start_time_the_app_and_an_id()
    {
        var (_, _, api, history) = Create();
        var track = Track();

        await history.ReportAsync(track, Start, 120, completed: false);

        var (_, play) = Assert.Single(api.Received);
        Assert.Equal((track.Id, 120.0, false, Start, "web"), (play.TrackId, play.SecondsPlayed, play.Completed, play.StartedAt, play.Client));
        Assert.NotNull(play.Id);
    }

    [Fact]
    public async Task A_play_too_short_to_count_is_not_sent()
    {
        var (_, _, api, history) = Create();

        await history.ReportAsync(Track(200), Start, 12, completed: false);

        Assert.Empty(api.Received);
    }

    [Fact]
    public async Task Offline_plays_wait_and_leave_in_order_at_the_next_start()
    {
        var (store, _, api, history) = Create(online: false);
        var first = Track();
        var second = Track();
        await history.ReportAsync(first, Start, 100, completed: true);
        await history.ReportAsync(second, Start.AddMinutes(4), 100, completed: true);
        Assert.Empty(api.Received);

        // Appli relancée, réseau revenu.
        var accounts = new AccountStore(store, multiAccount: false);
        var back = new FakeApi(accounts);
        await new PlayHistory(store, accounts, back, new Platform()).FlushAsync();

        Assert.Equal([first.Id, second.Id], back.Received.Select(r => r.Play.TrackId));
        Assert.Equal(api.Attempted[0].Id, back.Received[0].Play.Id);   // la même écoute, reconnue par le serveur
        await new PlayHistory(store, accounts, back, new Platform()).FlushAsync();
        Assert.Equal(2, back.Received.Count);   // partis une fois pour toutes
    }

    [Fact]
    public async Task A_play_the_server_refuses_is_dropped_instead_of_retried_forever()
    {
        var (_, _, api, history) = Create();
        api.Refuse = HttpStatusCode.NotFound;   // morceau supprimé entre-temps

        await history.ReportAsync(Track(), Start, 100, completed: true);
        api.Refuse = null;
        await history.FlushAsync();

        Assert.Empty(api.Received);
        Assert.Single(api.Attempted);
    }

    [Fact]
    public async Task A_server_error_keeps_the_play_for_later()
    {
        var (_, _, api, history) = Create();
        api.Refuse = HttpStatusCode.ServiceUnavailable;

        await history.ReportAsync(Track(), Start, 100, completed: true);
        api.Refuse = null;
        await history.FlushAsync();

        Assert.Single(api.Received);
    }

    [Fact]
    public async Task Each_account_s_waiting_plays_leave_under_that_account_only()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        await accounts.LoadAsync();
        var alice = await accounts.SaveSignedInAsync(Server, User("alice"));
        var bob = await accounts.SaveSignedInAsync(Server, User("bob"));
        await accounts.SwitchAsync(alice.Id);
        var api = new FakeApi(accounts) { Online = false };
        var history = new PlayHistory(store, accounts, api, new Platform());
        var track = Track();
        await history.ReportAsync(track, Start, 100, completed: true);

        api.Online = true;
        await accounts.SwitchAsync(bob.Id);
        await history.FlushAsync();
        Assert.Empty(api.Received);

        await accounts.SwitchAsync(alice.Id);
        await history.FlushAsync();
        Assert.Equal(("alice", track.Id), Assert.Single(api.Received) is var r ? (r.Account, r.Play.TrackId) : default);
    }

    [Fact]
    public async Task The_waiting_list_keeps_the_latest_plays_when_it_is_full()
    {
        var (store, _, api, history) = Create(online: false);
        var tracks = Enumerable.Range(0, PlayHistory.MaxPending + 3).Select(_ => Track()).ToList();
        foreach (var t in tracks) await history.ReportAsync(t, Start, 100, completed: true);

        api.Online = true;
        await history.FlushAsync();

        Assert.Equal(tracks.Skip(3).Select(t => t.Id), api.Received.Select(r => r.Play.TrackId));
    }

    [Fact]
    public void Listened_time_counts_what_was_heard_not_where_the_player_jumped()
    {
        var listen = new ListenTracker(Start, position: 0);
        listen.At(1);
        listen.At(2);
        listen.At(150);   // saut en avant : pas écouté
        listen.At(151);
        listen.At(10);    // retour en arrière : rien d'ajouté
        listen.At(11.5);

        Assert.Equal(4.5, listen.Seconds);
        Assert.Equal(Start, listen.StartedAt);
    }

    private static UserDto User(string name) => new(Guid.NewGuid(), name, Roles.User, DateTimeOffset.UnixEpoch);

    private sealed class Platform : IPlatformInfo
    {
        public bool HasMeteredNetworkDetection => false;
        public string Name => "web";
    }

    /// <summary>Comme ApiClient : la requête part avec le jeton du compte actif au moment de l'appel.</summary>
    private sealed class FakeApi(AccountStore accounts) : IPlayReportApi
    {
        public bool Online { get; set; } = true;
        public HttpStatusCode? Refuse { get; set; }
        public List<PlayEventRequest> Attempted { get; } = [];
        public List<(string Account, PlayEventRequest Play)> Received { get; } = [];

        public async Task ReportPlayAsync(PlayEventRequest play)
        {
            await Task.Yield();
            Attempted.Add(play);
            if (!Online) throw new HttpRequestException("offline");
            if (Refuse is { } status) throw new ApiException(status, "refused");
            Received.Add((accounts.Active?.Username ?? "", play));
        }
    }

    private sealed class MemoryStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _data = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(_data.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value)
        {
            if (value is null) _data.Remove(key);
            else _data[key] = value;
            return Task.CompletedTask;
        }
    }
}
