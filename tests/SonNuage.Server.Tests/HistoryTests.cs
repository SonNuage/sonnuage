using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Tests;

/// <summary>Historique d'écoute de chaque compte : écoutes envoyées par les applis, page Historique, statistiques, effacement.</summary>
public sealed class HistoryTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Chaque test a son compte : l'historique est propre à l'utilisateur.
    private async Task<(HttpClient Client, Guid UserId, List<TrackDto> Tracks)> ListenerAsync(string name)
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var created = await (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", Roles.User), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<UserDto>(Ct);
        var client = await factory.ClientAsync(name, $"{name}-password");
        var artists = await client.GetFromJsonAsync<PagedResult<ArtistDto>>("/api/artists?limit=50", Ct);
        var tracks = new List<TrackDto>();
        foreach (var artist in artists!.Items)
            tracks.AddRange((await client.GetFromJsonAsync<List<TrackDto>>($"/api/artists/{artist.Id}/tracks", Ct))!);
        return (client, created!.Id, tracks);
    }

    private static async Task<List<HistoryEntryDto>> HistoryAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PagedResult<HistoryEntryDto>>("/api/me/history?limit=100", Ct))!.Items.ToList();

    private async Task PlayedAsync(Guid userId, TrackDto track, DateTimeOffset at, double seconds, bool completed = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        db.PlayEvents.Add(new PlayEvent { UserId = userId, TrackId = track.Id, PlayedAt = at, SecondsPlayed = seconds, Completed = completed });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_reported_play_keeps_its_start_time_client_and_outcome()
    {
        var (client, _, tracks) = await ListenerAsync("history-report");
        var opening = tracks.Single(t => t.Title == "Opening");
        var started = DateTimeOffset.UtcNow.AddHours(-3);

        (await client.PostAsJsonAsync("/api/me/plays", new PlayEventRequest(opening.Id, 1.2, false, started, "android", Guid.NewGuid()), Ct))
            .EnsureSuccessStatusCode();

        var entry = Assert.Single(await HistoryAsync(client));
        Assert.Equal(("Opening", 1.2, false, "android"), (entry.Track.Title, entry.SecondsPlayed, entry.Completed, entry.Client));
        Assert.Equal(started.ToUnixTimeSeconds(), entry.StartedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task A_play_sent_twice_after_a_lost_answer_is_recorded_once()
    {
        var (client, _, tracks) = await ListenerAsync("history-twice");
        var play = new PlayEventRequest(tracks[0].Id, 2, true, DateTimeOffset.UtcNow.AddMinutes(-5), "web", Guid.NewGuid());

        (await client.PostAsJsonAsync("/api/me/plays", play, Ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/me/plays", play, Ct)).EnsureSuccessStatusCode();

        Assert.Single(await HistoryAsync(client));
    }

    [Fact]
    public async Task A_play_without_start_time_started_when_it_was_reported_minus_its_length_and_never_in_the_future()
    {
        var (client, _, tracks) = await ListenerAsync("history-times");
        var before = DateTimeOffset.UtcNow;
        (await client.PostAsJsonAsync("/api/me/plays", new PlayEventRequest(tracks[0].Id, 60, false), Ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/me/plays", new PlayEventRequest(tracks[0].Id, 1, true, DateTimeOffset.UtcNow.AddDays(2)), Ct))
            .EnsureSuccessStatusCode();

        var history = await HistoryAsync(client);
        Assert.All(history, e => Assert.True(e.StartedAt <= DateTimeOffset.UtcNow));
        Assert.Contains(history, e => e.SecondsPlayed == 60 && e.StartedAt <= before.AddSeconds(-59));
    }

    [Fact]
    public async Task History_lists_the_latest_listens_first_and_needs_a_session()
    {
        var (client, userId, tracks) = await ListenerAsync("history-order");
        var now = DateTimeOffset.UtcNow;
        await PlayedAsync(userId, tracks.Single(t => t.Title == "Opening"), now.AddDays(-2), 2);
        await PlayedAsync(userId, tracks.Single(t => t.Title == "Encore"), now.AddMinutes(-1), 1);

        Assert.Equal(["Encore", "Opening"], (await HistoryAsync(client)).Select(e => e.Track.Title));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/me/history", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().DeleteAsync("/api/me/history", Ct)).StatusCode);
    }

    [Fact]
    public async Task Nobody_sees_or_deletes_someone_else_s_history()
    {
        var (alice, aliceId, tracks) = await ListenerAsync("history-alice");
        var (bob, _, _) = await ListenerAsync("history-bob");
        await PlayedAsync(aliceId, tracks[0], DateTimeOffset.UtcNow.AddMinutes(-10), 2);
        var entry = Assert.Single(await HistoryAsync(alice));

        Assert.Empty(await HistoryAsync(bob));
        Assert.Equal(0, (await bob.GetFromJsonAsync<HistoryStatsDto>("/api/me/history/stats", Ct))!.Plays);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/me/history/{entry.Id}", Ct)).StatusCode);
        (await bob.DeleteAsync("/api/me/history", Ct)).EnsureSuccessStatusCode();

        Assert.Single(await HistoryAsync(alice));
    }

    [Fact]
    public async Task One_listen_or_the_whole_history_can_be_erased()
    {
        var (client, userId, tracks) = await ListenerAsync("history-erase");
        var now = DateTimeOffset.UtcNow;
        await PlayedAsync(userId, tracks[0], now.AddMinutes(-30), 2);
        await PlayedAsync(userId, tracks[1], now.AddMinutes(-20), 2);
        await PlayedAsync(userId, tracks[2], now.AddMinutes(-10), 2);

        var latest = (await HistoryAsync(client))[0];
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/me/history/{latest.Id}", Ct)).StatusCode);
        Assert.Equal(2, (await HistoryAsync(client)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me/history", Ct)).StatusCode);
        Assert.Empty(await HistoryAsync(client));
        Assert.Empty((await client.GetFromJsonAsync<HomeDto>("/api/home", Ct))!.RecentlyPlayed);
    }

    [Fact]
    public async Task Stats_sum_listening_time_and_rank_artists_and_tracks_over_the_period()
    {
        var (client, userId, tracks) = await ListenerAsync("history-stats");
        var now = DateTimeOffset.UtcNow;
        var opening = tracks.Single(t => t.Title == "Opening");
        var encore = tracks.Single(t => t.Title == "Encore");
        await PlayedAsync(userId, opening, now.AddDays(-1), 100);
        await PlayedAsync(userId, opening, now.AddDays(-2), 100);
        await PlayedAsync(userId, encore, now.AddDays(-3), 50);
        await PlayedAsync(userId, encore, now.AddDays(-20), 400);     // ce mois-ci, pas cette semaine
        await PlayedAsync(userId, encore, now.AddDays(-200), 1000);   // hors des deux

        var month = (await client.GetFromJsonAsync<HistoryStatsDto>("/api/me/history/stats?days=30", Ct))!;

        Assert.Equal((250.0, 650.0), (month.WeekSeconds, month.MonthSeconds));
        Assert.Equal(4, month.Plays);
        Assert.Equal([("Opening", 2), ("Encore", 2)], month.TopTracks.Select(t => (t.Track.Title, t.Plays)));
        Assert.Equal([("Test Artist", 2), ("Other Band", 2)], month.TopArtists.Select(a => (a.Artist.Name, a.Plays)));

        var week = (await client.GetFromJsonAsync<HistoryStatsDto>("/api/me/history/stats?days=7", Ct))!;
        Assert.Equal([("Opening", 2), ("Encore", 1)], week.TopTracks.Select(t => (t.Track.Title, t.Plays)));
        var all = (await client.GetFromJsonAsync<HistoryStatsDto>("/api/me/history/stats?days=0", Ct))!;
        Assert.Equal([("Encore", 3), ("Opening", 2)], all.TopTracks.Select(t => (t.Track.Title, t.Plays)));
    }

    [Fact]
    public async Task A_Subsonic_app_s_scrobble_is_recorded_with_the_app_s_name()
    {
        var (client, _, tracks) = await ListenerAsync("history-subsonic");
        var access = (await (await client.PostAsync("/api/me/subsonic", null, Ct)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<SubsonicAccessDto>(Ct))!;
        var song = tracks.Single(t => t.Title == "Opening");
        var started = DateTimeOffset.UtcNow.AddMinutes(-7);

        var body = await factory.CreateClient().GetStringAsync(
            $"/rest/scrobble.view?u=history-subsonic&p={Uri.EscapeDataString(access.Password!)}&v=1.16.1&c=Symfonium&f=json"
            + $"&id=tr-{song.Id:N}&time={started.ToUnixTimeMilliseconds()}&submission=true", Ct);
        Assert.Equal("ok", JsonDocument.Parse(body).RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());

        var entry = Assert.Single(await HistoryAsync(client));
        Assert.Equal(("Symfonium", true), (entry.Client, entry.Completed));
        Assert.Equal(started.ToUnixTimeSeconds(), entry.StartedAt.ToUnixTimeSeconds());
    }
}
