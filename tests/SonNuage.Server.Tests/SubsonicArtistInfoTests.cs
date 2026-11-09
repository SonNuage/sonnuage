using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;

namespace SonNuage.Server.Tests;

/// <summary>Artistes proches fournis par un faux ListenBrainz : seuls ceux de la bibliothèque comptent.</summary>
public sealed class SimilarArtistsFactory : SonNuageFactory
{
    public int Calls;

    protected override void ConfigureMore(IWebHostBuilder builder) =>
        builder.ConfigureServices(s => s.AddSingleton<ISimilarArtistSource>(new Fake(this)));

    private sealed class Fake(SimilarArtistsFactory owner) : ISimilarArtistSource
    {
        public Task<IReadOnlyList<SimilarArtist>> SimilarArtistsAsync(string artistMbid, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Calls);
            IReadOnlyList<SimilarArtist> list = artistMbid == "mbid-test"
                ? [new("mbid-elsewhere", "Pas dans la bibliothèque", null, 0.9), new("mbid-other", "Other Band", null, 0.5)]
                : [];
            return Task.FromResult(list);
        }
    }
}

public sealed class SubsonicArtistInfoTests(SimilarArtistsFactory factory) : IClassFixture<SimilarArtistsFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Func<string, string, Task<JsonElement>>> SessionAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            await db.Artists.Where(a => a.Name == "Test Artist").ExecuteUpdateAsync(s => s.SetProperty(a => a.MusicBrainzId, "mbid-test"), Ct);
            await db.Artists.Where(a => a.Name == "Other Band").ExecuteUpdateAsync(s => s.SetProperty(a => a.MusicBrainzId, "mbid-other"), Ct);
        }
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

    private static async Task<Dictionary<string, string>> ArtistIdsAsync(Func<string, string, Task<JsonElement>> call) =>
        (await call("getArtists", "")).GetProperty("artists").GetProperty("index").EnumerateArray()
            .SelectMany(i => i.GetProperty("artist").EnumerateArray())
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetString()!);

    [Fact]
    public async Task Artist_info_lists_similar_artists_of_the_library_once_a_day()
    {
        var call = await SessionAsync();
        var ids = await ArtistIdsAsync(call);

        var info = (await call("getArtistInfo2", $"id={ids["Test Artist"]}")).GetProperty("artistInfo2");

        Assert.Equal("mbid-test", info.GetProperty("musicBrainzId").GetString());
        var similar = Assert.Single(info.GetProperty("similarArtist").EnumerateArray());
        Assert.Equal(ids["Other Band"], similar.GetProperty("id").GetString());
        Assert.Equal("Other Band", similar.GetProperty("name").GetString());

        var legacy = (await call("getArtistInfo", $"id={ids["Test Artist"]}")).GetProperty("artistInfo");
        Assert.Single(legacy.GetProperty("similarArtist").EnumerateArray());
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task An_artist_without_similar_artists_still_gets_an_answer()
    {
        var call = await SessionAsync();
        var ids = await ArtistIdsAsync(call);

        var info = (await call("getArtistInfo2", $"id={ids["Other Band"]}")).GetProperty("artistInfo2");

        Assert.Empty(info.GetProperty("similarArtist").EnumerateArray());
    }

    [Fact]
    public async Task Now_playing_shows_what_is_being_listened_to_until_it_ends()
    {
        var call = await SessionAsync();
        var song = (await call("search3", "query=opening")).GetProperty("searchResult3").GetProperty("song")[0].GetProperty("id").GetString();

        await call("scrobble", $"id={song}&submission=false");
        var entry = Assert.Single((await call("getNowPlaying", "")).GetProperty("nowPlaying").GetProperty("entry").EnumerateArray());
        Assert.Equal("Opening", entry.GetProperty("title").GetString());
        Assert.Equal("admin", entry.GetProperty("username").GetString());
        Assert.Equal("tests", entry.GetProperty("playerName").GetString());
        Assert.Equal(0, entry.GetProperty("minutesAgo").GetInt32());

        await call("scrobble", $"id={song}&submission=true");
        Assert.Empty((await call("getNowPlaying", "")).GetProperty("nowPlaying").GetProperty("entry").EnumerateArray());
    }
}
