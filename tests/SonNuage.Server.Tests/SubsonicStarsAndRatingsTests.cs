using System.Net.Http.Json;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Favoris d'albums et d'artistes, notes de 1 à 5 sur les titres, albums et artistes.</summary>
public sealed class SubsonicStarsAndRatingsTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Func<string, string, Task<JsonElement>>> SessionAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var access = (await (await admin.PostAsync("/api/me/subsonic", null, Ct)).Content.ReadFromJsonAsync<SubsonicAccessDto>(Ct))!;
        var client = factory.CreateClient();
        return async (method, query) =>
        {
            var text = await client.GetStringAsync($"/rest/{method}?apiKey={access.Password}&v=1.16.1&c=tests&f=json&{query}", Ct);
            return JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").Clone();
        };
    }

    private static void AssertOk(JsonElement response) => Assert.True(response.GetProperty("status").GetString() == "ok", response.ToString());

    private static async Task<(string Artist, string Album, string Song)> IdsAsync(Func<string, string, Task<JsonElement>> call)
    {
        var result = (await call("search3", "query=opening")).GetProperty("searchResult3");
        var song = result.GetProperty("song")[0];
        return (song.GetProperty("artistId").GetString()!, song.GetProperty("albumId").GetString()!, song.GetProperty("id").GetString()!);
    }

    private static IEnumerable<string?> Ids(JsonElement parent, string list) =>
        parent.TryGetProperty(list, out var items) ? items.EnumerateArray().Select(i => i.GetProperty("id").GetString()) : [];

    [Fact]
    public async Task Albums_and_artists_can_be_starred_and_unstarred()
    {
        var call = await SessionAsync();
        var (artist, album, _) = await IdsAsync(call);

        AssertOk(await call("star", $"albumId={album}&artistId={artist}"));

        var starred = (await call("getStarred2", "")).GetProperty("starred2");
        Assert.Contains(album, Ids(starred, "album"));
        Assert.Contains(artist, Ids(starred, "artist"));
        Assert.True((await call("getAlbum", $"id={album}")).GetProperty("album").TryGetProperty("starred", out _));
        Assert.True((await call("getArtist", $"id={artist}")).GetProperty("artist").TryGetProperty("starred", out _));
        Assert.Contains(album, Ids((await call("getAlbumList2", "type=starred")).GetProperty("albumList2"), "album"));
        Assert.Contains(album, Ids((await call("getStarred", "")).GetProperty("starred"), "album"));

        // Les anciens clients passent l'album par « id ».
        AssertOk(await call("unstar", $"id={album}&artistId={artist}"));
        var after = (await call("getStarred2", "")).GetProperty("starred2");
        Assert.DoesNotContain(album, Ids(after, "album"));
        Assert.DoesNotContain(artist, Ids(after, "artist"));
    }

    [Fact]
    public async Task Ratings_are_kept_per_item_and_drive_the_highest_rated_list()
    {
        var call = await SessionAsync();
        var (artist, album, song) = await IdsAsync(call);

        AssertOk(await call("setRating", $"id={song}&rating=4"));
        AssertOk(await call("setRating", $"id={album}&rating=5"));
        AssertOk(await call("setRating", $"id={artist}&rating=3"));

        Assert.Equal(4, (await call("getSong", $"id={song}")).GetProperty("song").GetProperty("userRating").GetInt32());
        Assert.Equal(5, (await call("getAlbum", $"id={album}")).GetProperty("album").GetProperty("userRating").GetInt32());
        Assert.Equal(3, (await call("getArtist", $"id={artist}")).GetProperty("artist").GetProperty("userRating").GetInt32());
        Assert.Equal([album], Ids((await call("getAlbumList2", "type=highest")).GetProperty("albumList2"), "album"));

        // 0 retire la note ; une note hors de 0 à 5 est refusée.
        AssertOk(await call("setRating", $"id={album}&rating=0"));
        Assert.False((await call("getAlbum", $"id={album}")).GetProperty("album").TryGetProperty("userRating", out _));
        Assert.Empty(Ids((await call("getAlbumList2", "type=highest")).GetProperty("albumList2"), "album"));
        Assert.Equal("failed", (await call("setRating", $"id={song}&rating=6")).GetProperty("status").GetString());
        Assert.Equal(70, (await call("setRating", $"id=tr-{Guid.NewGuid():N}&rating=2")).GetProperty("error").GetProperty("code").GetInt32());
    }
}
