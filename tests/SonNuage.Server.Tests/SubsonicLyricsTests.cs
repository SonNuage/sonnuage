using System.Net.Http.Json;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Paroles par l'API Subsonic : getLyrics (texte brut) et getLyricsBySongId (extension OpenSubsonic songLyrics).</summary>
public sealed class SubsonicLyricsTests(LyricsLibraryFactory factory) : IClassFixture<LyricsLibraryFactory>
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

    private static async Task<string> SongIdAsync(Func<string, string, Task<JsonElement>> call, string query) =>
        (await call("search3", $"query={query}&artistCount=0&albumCount=0"))
            .GetProperty("searchResult3").GetProperty("song")[0].GetProperty("id").GetString()!;

    [Fact]
    public async Task Synced_lyrics_come_back_line_by_line_in_milliseconds()
    {
        var call = await SessionAsync();
        var id = await SongIdAsync(call, "opening");

        var lyrics = (await call("getLyricsBySongId", $"id={id}")).GetProperty("lyricsList").GetProperty("structuredLyrics");

        var only = Assert.Single(lyrics.EnumerateArray());
        Assert.True(only.GetProperty("synced").GetBoolean());
        Assert.Equal("Opening", only.GetProperty("displayTitle").GetString());
        Assert.Equal("Test Artist", only.GetProperty("displayArtist").GetString());
        Assert.Equal("und", only.GetProperty("lang").GetString());
        var lines = only.GetProperty("line").EnumerateArray().Select(l => (l.GetProperty("start").GetInt64(), l.GetProperty("value").GetString())).ToList();
        Assert.Equal([(500L, "Première ligne"), (1200L, "Seconde ligne")], lines);
    }

    [Fact]
    public async Task A_song_without_lyrics_has_an_empty_list()
    {
        var call = await SessionAsync();
        var id = await SongIdAsync(call, "encore");

        var lyrics = (await call("getLyricsBySongId", $"id={id}")).GetProperty("lyricsList").GetProperty("structuredLyrics");

        Assert.Empty(lyrics.EnumerateArray());
    }

    [Fact]
    public async Task Legacy_clients_get_plain_text_by_artist_and_title()
    {
        var call = await SessionAsync();

        var lyrics = (await call("getLyrics", "artist=test%20artist&title=OPENING")).GetProperty("lyrics");

        Assert.Equal("Test Artist", lyrics.GetProperty("artist").GetString());
        Assert.Equal("Opening", lyrics.GetProperty("title").GetString());
        Assert.Equal("Première ligne\nSeconde ligne", lyrics.GetProperty("value").GetString());

        var unknown = (await call("getLyrics", "artist=nobody&title=nothing")).GetProperty("lyrics");
        Assert.False(unknown.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task The_song_lyrics_extension_is_announced()
    {
        var call = await SessionAsync();

        var names = (await call("getOpenSubsonicExtensions", "")).GetProperty("openSubsonicExtensions").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString());

        Assert.Contains("songLyrics", names);
    }
}
