using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Côté appareil : fondu et lecture infinie viennent du compte, la copie locale sert hors ligne.</summary>
public class PlaybackSettingsSyncTests
{
    private static readonly Uri Server = new("https://music.example.org/");

    [Fact]
    public async Task Loading_takes_the_account_s_preferences_and_keeps_a_local_copy()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: false);
        var api = new FakeApi { Remote = new PlaybackPreferences(2.5, true, 6) };
        var settings = new PlaybackSettings(store, accounts, api);

        await settings.LoadAsync();

        Assert.Equal((2.5, true, 6), (settings.CrossfadeSeconds, settings.InfinitePlay, settings.InfiniteAhead));
        // Hors ligne au démarrage suivant : la copie locale prend le relais.
        var offline = new PlaybackSettings(store, new AccountStore(store, multiAccount: false), new FakeApi { Fails = true });
        await offline.LoadAsync();
        Assert.Equal((2.5, true, 6), (offline.CrossfadeSeconds, offline.InfinitePlay, offline.InfiniteAhead));
    }

    [Fact]
    public async Task Saving_sends_the_preferences_to_the_account()
    {
        var store = new MemoryStore();
        var api = new FakeApi { Remote = PlaybackPreferences.Default };
        var settings = new PlaybackSettings(store, new AccountStore(store, multiAccount: false), api);
        await settings.LoadAsync();

        await settings.SaveAsync(StreamQuality.High, StreamQuality.Low, 4, 1.26, true, 2);

        Assert.Equal(new PlaybackPreferences(1.3, true, 2), api.Remote);
        Assert.Equal(StreamQuality.High, settings.Quality);
    }

    [Fact]
    public async Task An_answer_for_an_account_that_is_no_longer_active_is_ignored()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        await accounts.LoadAsync();
        var alice = await accounts.SaveSignedInAsync(Server, User("alice"));
        var bob = await accounts.SaveSignedInAsync(Server, User("bob"));
        await accounts.SwitchAsync(alice.Id);
        var api = new PerAccountApi(accounts);
        api.Remote["alice"] = new PlaybackPreferences(4, true, 9);
        api.Remote["bob"] = PlaybackPreferences.Default;
        var settings = new PlaybackSettings(store, accounts, api);

        var aliceLoad = settings.LoadAsync();        // La réponse pour Alice tarde…
        await accounts.SwitchAsync(bob.Id);          // … on passe sur Bob (rechargement automatique),
        api.Release("bob");
        await Task.Delay(50);
        api.Release("alice");                        // … puis celle d'Alice arrive : elle ne doit pas s'appliquer.
        await aliceLoad;

        Assert.Equal((0.0, false, PlaybackPreferences.DefaultAhead), (settings.CrossfadeSeconds, settings.InfinitePlay, settings.InfiniteAhead));
    }

    /// <summary>Comme ApiClient : la requête part avec le jeton du compte actif au moment de l'appel.</summary>
    private sealed class PerAccountApi(AccountStore accounts) : IPlaybackPreferencesApi
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = [];
        public Dictionary<string, PlaybackPreferences> Remote { get; } = [];

        private TaskCompletionSource Gate(string user) =>
            _gates.TryGetValue(user, out var g) ? g : _gates[user] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(string user) => Gate(user).TrySetResult();

        public async Task<PlaybackPreferences?> PlaybackPreferencesAsync()
        {
            var user = accounts.Active!.Username;
            await Gate(user).Task;
            return Remote[user];
        }

        public Task<PlaybackPreferences?> SavePlaybackPreferencesAsync(PlaybackPreferences prefs) => Task.FromResult<PlaybackPreferences?>(prefs);
    }

    private static UserDto User(string name) => new(Guid.NewGuid(), name, Roles.User, DateTimeOffset.UnixEpoch);

    private sealed class FakeApi : IPlaybackPreferencesApi
    {
        public PlaybackPreferences? Remote { get; set; }
        public bool Fails { get; init; }
        public async Task<PlaybackPreferences?> PlaybackPreferencesAsync()
        {
            await Task.Yield();
            if (Fails) throw new HttpRequestException("offline");
            return Remote;
        }

        public Task<PlaybackPreferences?> SavePlaybackPreferencesAsync(PlaybackPreferences prefs)
        {
            if (Fails) throw new HttpRequestException("offline");
            Remote = prefs.Clamped();
            return Task.FromResult(Remote);
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
