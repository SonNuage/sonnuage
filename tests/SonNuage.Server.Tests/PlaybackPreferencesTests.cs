using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Fondu et lecture infinie suivent le compte sur tous ses appareils.</summary>
public sealed class PlaybackPreferencesTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> UserAsync(string name)
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        return await factory.ClientAsync(name, $"{name}-password");
    }

    [Fact]
    public async Task A_new_account_gets_the_defaults()
    {
        var user = await UserAsync("prefs-new");
        var prefs = await user.GetFromJsonAsync<PlaybackPreferences>("/api/me/playback", Ct);
        Assert.Equal(PlaybackPreferences.Default, prefs);
    }

    [Fact]
    public async Task Saved_preferences_follow_the_account_to_another_device_and_stay_its_own()
    {
        var phone = await UserAsync("prefs-owner");
        (await phone.PutAsJsonAsync("/api/me/playback", new PlaybackPreferences(2.5, true, 5), Ct)).EnsureSuccessStatusCode();

        // Une autre connexion du même compte (un autre appareil) retrouve les réglages.
        var desktop = await factory.ClientAsync("prefs-owner", "prefs-owner-password");
        Assert.Equal(new PlaybackPreferences(2.5, true, 5), await desktop.GetFromJsonAsync<PlaybackPreferences>("/api/me/playback", Ct));

        var other = await UserAsync("prefs-other");
        Assert.Equal(PlaybackPreferences.Default, await other.GetFromJsonAsync<PlaybackPreferences>("/api/me/playback", Ct));
    }

    [Fact]
    public async Task Out_of_range_values_are_brought_back_into_range()
    {
        var user = await UserAsync("prefs-clamp");
        var saved = await (await user.PutAsJsonAsync("/api/me/playback", new PlaybackPreferences(9.87, true, 40), Ct))
            .Content.ReadFromJsonAsync<PlaybackPreferences>(Ct);
        Assert.Equal(new PlaybackPreferences(PlaybackPreferences.MaxCrossfadeSeconds, true, PlaybackPreferences.MaxAhead), saved);

        saved = await (await user.PutAsJsonAsync("/api/me/playback", new PlaybackPreferences(1.234, false, 0), Ct))
            .Content.ReadFromJsonAsync<PlaybackPreferences>(Ct);
        Assert.Equal(new PlaybackPreferences(1.2, false, 1), saved);
    }
}
