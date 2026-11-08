using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Titres phares d'un artiste et morceaux proposés par la lecture infinie.</summary>
public sealed class DiscoveryTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Admin, List<TrackDto> Tracks)> LibraryAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var artists = await admin.GetFromJsonAsync<PagedResult<ArtistDto>>("/api/artists?limit=50", Ct);
        var tracks = new List<TrackDto>();
        foreach (var artist in artists!.Items)
            tracks.AddRange((await admin.GetFromJsonAsync<List<TrackDto>>($"/api/artists/{artist.Id}/tracks", Ct))!);
        return (admin, tracks);
    }

    private static async Task SetGenreAsync(HttpClient admin, TrackDto track, string genre) =>
        (await admin.PutAsJsonAsync($"/api/admin/tracks/{track.Id}/metadata",
            new UpdateTrackMetadataRequest(null, null, null, null, track.TrackNumber, track.DiscNumber, null, genre), Ct)).EnsureSuccessStatusCode();

    [Fact]
    public async Task An_artist_s_top_tracks_are_the_most_played_first()
    {
        var (admin, tracks) = await LibraryAsync();
        var opening = tracks.Single(t => t.Title == "Opening");
        var second = tracks.Single(t => t.Title == "Second Song");
        for (var i = 0; i < 2; i++)
            (await admin.PostAsJsonAsync("/api/me/plays", new PlayEventRequest(second.Id, 3, true), Ct)).EnsureSuccessStatusCode();

        var top = await admin.GetFromJsonAsync<List<TrackDto>>($"/api/artists/{opening.ArtistId}/top?limit=10", Ct);
        Assert.Equal(["Second Song", "Opening"], top!.Select(t => t.Title));
        var one = await admin.GetFromJsonAsync<List<TrackDto>>($"/api/artists/{opening.ArtistId}/top?limit=1", Ct);
        Assert.Equal(["Second Song"], one!.Select(t => t.Title));
    }

    [Fact]
    public async Task Infinite_play_picks_the_same_artist_then_never_repeats_what_is_excluded()
    {
        var (admin, tracks) = await LibraryAsync();
        var opening = tracks.Single(t => t.Title == "Opening");
        // Genre propre à ce test : l'autre test de lecture infinie partage la même bibliothèque et y met un genre commun.
        await SetGenreAsync(admin, opening, "Genre seul");

        var next = await (await admin.PostAsJsonAsync("/api/radio", new RadioRequest([opening.Id], [opening.Id], 1), Ct))
            .Content.ReadFromJsonAsync<List<TrackDto>>(Ct);
        Assert.Equal(["Second Song"], next!.Select(t => t.Title));

        var all = await (await admin.PostAsJsonAsync("/api/radio", new RadioRequest([opening.Id], [opening.Id], 10), Ct))
            .Content.ReadFromJsonAsync<List<TrackDto>>(Ct);
        Assert.DoesNotContain(all!, t => t.Id == opening.Id);
        Assert.Equal(tracks.Count - 1, all.Count);
        Assert.Equal("Second Song", all[0].Title);
    }

    [Fact]
    public async Task Infinite_play_prefers_a_shared_genre_over_the_same_artist()
    {
        var (admin, tracks) = await LibraryAsync();
        var opening = tracks.Single(t => t.Title == "Opening");
        var encore = tracks.Single(t => t.Title == "Encore");
        foreach (var t in new[] { opening, encore }) await SetGenreAsync(admin, t, "Rock progressif");

        var next = await (await admin.PostAsJsonAsync("/api/radio", new RadioRequest([opening.Id], [opening.Id], 1), Ct))
            .Content.ReadFromJsonAsync<List<TrackDto>>(Ct);
        Assert.Equal(["Encore"], next!.Select(t => t.Title));
    }
}
