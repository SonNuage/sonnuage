using System.Net.Http.Json;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Mix (titres similaires), file de lecture partagée entre appareils, date de synchronisation des index.</summary>
public sealed class SubsonicQueueAndMixTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
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
            var response = JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").Clone();
            Assert.True(response.GetProperty("status").GetString() == "ok", response.ToString());
            return response;
        };
    }

    private static async Task<Dictionary<string, string>> SongIdsAsync(Func<string, string, Task<JsonElement>> call) =>
        (await call("search3", "query=&artistCount=0&albumCount=0&songCount=50")).GetProperty("searchResult3").GetProperty("song")
            .EnumerateArray().ToDictionary(s => s.GetProperty("title").GetString()!, s => s.GetProperty("id").GetString()!);

    private static List<string> Titles(JsonElement list) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(s => s.GetProperty("title").GetString()!).ToList() : [];

    [Fact]
    public async Task A_mix_from_a_song_starts_with_the_same_artist_and_skips_the_seed()
    {
        var call = await SessionAsync();
        var ids = await SongIdsAsync(call);

        var mix = Titles((await call("getSimilarSongs2", $"id={ids["Opening"]}&count=10")).GetProperty("similarSongs2").GetProperty("song"));

        Assert.Equal(["Second Song", "Encore"], mix);
        var legacy = Titles((await call("getSimilarSongs", $"id={ids["Opening"]}&count=1")).GetProperty("similarSongs").GetProperty("song"));
        Assert.Equal(["Second Song"], legacy);
    }

    [Fact]
    public async Task A_mix_from_an_artist_or_an_album_draws_from_the_library()
    {
        var call = await SessionAsync();
        var artistId = (await call("getArtists", "")).GetProperty("artists").GetProperty("index").EnumerateArray()
            .SelectMany(i => i.GetProperty("artist").EnumerateArray()).First(a => a.GetProperty("name").GetString() == "Test Artist")
            .GetProperty("id").GetString();

        var mix = Titles((await call("getSimilarSongs2", $"id={artistId}&count=2")).GetProperty("similarSongs2").GetProperty("song"));

        Assert.Equal(2, mix.Count);
        Assert.All(mix, t => Assert.Contains(t, new[] { "Opening", "Second Song" }));
    }

    [Fact]
    public async Task The_play_queue_follows_the_account_from_one_device_to_another()
    {
        var call = await SessionAsync();
        var ids = await SongIdsAsync(call);
        await call("savePlayQueue", "");

        Assert.False((await call("getPlayQueue", "")).TryGetProperty("playQueue", out _));

        await call("savePlayQueue", $"id={ids["Opening"]}&id={ids["Encore"]}&current={ids["Encore"]}&position=1500");
        var queue = (await call("getPlayQueue", "")).GetProperty("playQueue");

        Assert.Equal(["Opening", "Encore"], Titles(queue.GetProperty("entry")));
        Assert.Equal(ids["Encore"], queue.GetProperty("current").GetString());
        Assert.Equal(1500, queue.GetProperty("position").GetInt64());
        Assert.Equal("admin", queue.GetProperty("username").GetString());
        Assert.Equal("tests", queue.GetProperty("changedBy").GetString());
        Assert.True(queue.TryGetProperty("changed", out _));

        // Une file vide efface celle du compte.
        await call("savePlayQueue", "");
        Assert.False((await call("getPlayQueue", "")).TryGetProperty("playQueue", out _));
    }

    [Fact]
    public async Task Indexes_only_change_date_when_the_library_changes()
    {
        var call = await SessionAsync();

        var first = (await call("getIndexes", "")).GetProperty("indexes");
        await Task.Delay(20, Ct);
        var second = (await call("getIndexes", "")).GetProperty("indexes");
        var lastModified = first.GetProperty("lastModified").GetInt64();

        Assert.Equal(lastModified, second.GetProperty("lastModified").GetInt64());
        Assert.NotEmpty(second.GetProperty("index").EnumerateArray());

        // Rien de nouveau depuis la dernière synchronisation : pas de liste à relire.
        var unchanged = (await call("getIndexes", $"ifModifiedSince={lastModified}")).GetProperty("indexes");
        Assert.Equal(lastModified, unchanged.GetProperty("lastModified").GetInt64());
        Assert.Empty(unchanged.GetProperty("index").EnumerateArray());
    }
}
