using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Tests;

/// <summary>Découverte dans la bibliothèque : « À réécouter » et « Pas encore écouté » sur l'accueil.</summary>
public sealed class HomeDiscoveryTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Chaque test a son compte : l'historique d'écoute est propre à l'utilisateur.
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

    private async Task PlayedAsync(Guid userId, TrackDto track, int daysAgo, int times = 1)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        for (var i = 0; i < times; i++)
            db.PlayEvents.Add(new PlayEvent
            {
                UserId = userId, TrackId = track.Id, PlayedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(-i),
                SecondsPlayed = track.DurationSeconds, Completed = true,
            });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<HomeDto> HomeAsync(HttpClient client) => (await client.GetFromJsonAsync<HomeDto>("/api/home", Ct))!;

    [Fact]
    public async Task Tracks_played_often_but_not_lately_are_offered_again()
    {
        var (client, userId, tracks) = await ListenerAsync("rediscover-often");
        var opening = tracks.Single(t => t.Title == "Opening");
        var encore = tracks.Single(t => t.Title == "Encore");
        await PlayedAsync(userId, opening, daysAgo: 45, times: 3);
        await PlayedAsync(userId, encore, daysAgo: 1, times: 3);   // écouté hier : pas encore « à réécouter »

        var home = await HomeAsync(client);

        Assert.Equal(["Opening"], home.Rediscover!.Select(t => t.Title));
    }

    [Fact]
    public async Task A_track_played_once_long_ago_is_not_a_favourite_to_rediscover()
    {
        var (client, userId, tracks) = await ListenerAsync("rediscover-once");
        await PlayedAsync(userId, tracks.Single(t => t.Title == "Opening"), daysAgo: 60);

        Assert.Empty((await HomeAsync(client)).Rediscover!);
    }

    [Fact]
    public async Task A_track_liked_long_ago_and_not_played_lately_is_offered_again()
    {
        var (client, userId, tracks) = await ListenerAsync("rediscover-liked");
        var encore = tracks.Single(t => t.Title == "Encore");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            db.Favorites.Add(new Favorite { UserId = userId, TrackId = encore.Id, CreatedAt = DateTimeOffset.UtcNow.AddDays(-60) });
            await db.SaveChangesAsync(Ct);
        }
        // Liké à l'instant : on vient d'y penser, ce n'est pas « à réécouter ».
        (await client.PutAsync($"/api/me/favorites/{tracks.Single(t => t.Title == "Opening").Id}", null, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(["Encore"], (await HomeAsync(client)).Rediscover!.Select(t => t.Title));
    }

    [Fact]
    public async Task Albums_never_played_are_offered_and_played_ones_are_not()
    {
        var (client, userId, tracks) = await ListenerAsync("not-yet-played");
        var opening = tracks.Single(t => t.Title == "Opening");
        await PlayedAsync(userId, opening, daysAgo: 0);

        var home = await HomeAsync(client);

        Assert.DoesNotContain(home.NotYetPlayed!, a => a.Id == opening.AlbumId);
        Assert.Contains(home.NotYetPlayed!, a => a.Title == "Live");
    }

    [Fact]
    public async Task Without_any_history_nothing_is_offered_twice()
    {
        // Rien écouté : « Pas encore écouté » répéterait « Ajouts récents ».
        var (client, _, _) = await ListenerAsync("no-history");

        var home = await HomeAsync(client);

        Assert.Empty(home.NotYetPlayed!);
        Assert.Empty(home.Rediscover!);
    }
}
