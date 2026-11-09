using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Favoris d'albums et d'artistes, notes de 1 à 5 : les mêmes que dans les applis Subsonic.</summary>
public sealed class FavoriteCollectionsTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, TrackDto Track)> SessionAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var search = await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=opening", Ct);
        return (admin, search!.Tracks[0]);
    }

    [Fact]
    public async Task An_album_and_an_artist_can_be_added_to_favorites_and_removed()
    {
        var (client, track) = await SessionAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/api/me/favorites/albums/{track.AlbumId}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/api/me/favorites/artists/{track.ArtistId}", null, Ct)).StatusCode);
        // Deux fois de suite : pas d'erreur, pas de doublon.
        await client.PutAsync($"/api/me/favorites/albums/{track.AlbumId}", null, Ct);

        var albums = await client.GetFromJsonAsync<List<AlbumDto>>("/api/me/favorites/albums", Ct);
        Assert.Equal(track.AlbumId, Assert.Single(albums!).Id);
        var artists = await client.GetFromJsonAsync<List<ArtistDto>>("/api/me/favorites/artists", Ct);
        Assert.Equal(track.ArtistId, Assert.Single(artists!).Id);
        Assert.True((await client.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{track.AlbumId}", Ct))!.IsFavorite);
        Assert.True((await client.GetFromJsonAsync<ArtistDetailDto>($"/api/artists/{track.ArtistId}", Ct))!.IsFavorite);

        await client.DeleteAsync($"/api/me/favorites/albums/{track.AlbumId}", Ct);
        await client.DeleteAsync($"/api/me/favorites/artists/{track.ArtistId}", Ct);
        Assert.Empty((await client.GetFromJsonAsync<List<AlbumDto>>("/api/me/favorites/albums", Ct))!);
        Assert.False((await client.GetFromJsonAsync<ArtistDetailDto>($"/api/artists/{track.ArtistId}", Ct))!.IsFavorite);
    }

    [Fact]
    public async Task A_missing_album_cannot_be_added_to_favorites()
    {
        var (client, _) = await SessionAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/api/me/favorites/albums/{Guid.NewGuid()}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/api/me/favorites/artists/{Guid.NewGuid()}", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Tracks_albums_and_artists_get_a_rating_that_zero_removes()
    {
        var (client, track) = await SessionAsync();

        await client.PutAsJsonAsync($"/api/me/ratings/{track.Id}", new RatingRequest(4), Ct);
        await client.PutAsJsonAsync($"/api/me/ratings/{track.AlbumId}", new RatingRequest(5), Ct);
        await client.PutAsJsonAsync($"/api/me/ratings/{track.ArtistId}", new RatingRequest(3), Ct);

        Assert.Equal(4, (await client.GetFromJsonAsync<TrackDto>($"/api/tracks/{track.Id}", Ct))!.Rating);
        Assert.Equal(5, (await client.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{track.AlbumId}", Ct))!.Rating);
        Assert.Equal(3, (await client.GetFromJsonAsync<ArtistDetailDto>($"/api/artists/{track.ArtistId}", Ct))!.Rating);

        await client.PutAsJsonAsync($"/api/me/ratings/{track.Id}", new RatingRequest(0), Ct);
        Assert.Null((await client.GetFromJsonAsync<TrackDto>($"/api/tracks/{track.Id}", Ct))!.Rating);
    }

    [Fact]
    public async Task A_rating_outside_one_to_five_or_on_a_missing_item_is_refused()
    {
        var (client, track) = await SessionAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/me/ratings/{track.Id}", new RatingRequest(6), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/me/ratings/{track.Id}", new RatingRequest(-1), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/me/ratings/{Guid.NewGuid()}", new RatingRequest(3), Ct)).StatusCode);
    }
}
