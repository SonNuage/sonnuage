using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Nom de l'appareil montré aux autres appareils du compte.</summary>
public sealed class DeviceIdentityTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0", "Firefox · Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36 Edg/131.0", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Mobile Safari/537.36", "Chrome · Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1", "Safari · iPhone")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15", "Safari · macOS")]
    [InlineData("", "Navigateur")]
    public void A_browser_is_named_after_itself_and_its_system(string userAgent, string name) =>
        Assert.Equal(name, DeviceNames.FromUserAgent(userAgent));

    [Fact]
    public async Task The_chosen_name_wins_over_the_system_one_and_can_be_cleared()
    {
        var store = new MemoryStore();
        var identity = new DeviceIdentity(store, new Platform("Pixel 8"));
        await identity.LoadAsync();
        Assert.Equal("Pixel 8", identity.Name);

        var changes = 0;
        identity.Changed += () => changes++;
        await identity.RenameAsync("  Téléphone de Phil  ");
        Assert.Equal(("Téléphone de Phil", "Téléphone de Phil"), (identity.Name, identity.CustomName));
        Assert.Equal("Téléphone de Phil", (await new DeviceIdentity(store, new Platform("Pixel 8")).LoadThenNameAsync()));

        await identity.RenameAsync(" ");
        Assert.Equal(("Pixel 8", (string?)null), (identity.Name, identity.CustomName));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Without_a_system_name_the_platform_names_the_device()
    {
        var identity = new DeviceIdentity(new MemoryStore(), new Platform(null));
        await identity.LoadAsync();
        Assert.Equal("test", identity.Name);
        await identity.RenameAsync(new string('x', 100));
        Assert.Equal(60, identity.Name.Length);
    }

    private sealed class Platform(string? deviceName) : IPlatformInfo
    {
        public bool HasMeteredNetworkDetection => false;
        public string Name => "test";
        public Task<string?> DeviceNameAsync() => Task.FromResult(deviceName);
    }

    private sealed class MemoryStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
            return Task.CompletedTask;
        }
    }
}

internal static class DeviceIdentityTestExtensions
{
    public static async Task<string> LoadThenNameAsync(this DeviceIdentity identity)
    {
        await identity.LoadAsync();
        return identity.Name;
    }
}
