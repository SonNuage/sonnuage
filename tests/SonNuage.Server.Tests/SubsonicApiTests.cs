using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>API Subsonic (<c>/rest/*</c>) et mot de passe Subsonic du compte (Réglages).</summary>
public sealed class SubsonicApiTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_user_generates_reads_and_revokes_a_subsonic_password()
    {
        var admin = await factory.AdminClientAsync();
        await admin.DeleteAsync("/api/me/subsonic", Ct);

        var none = (await admin.GetFromJsonAsync<SubsonicAccessDto>("/api/me/subsonic", Ct))!;
        Assert.False(none.Enabled);
        Assert.Null(none.Password);

        var created = await admin.PostAsync("/api/me/subsonic", null, Ct);
        created.EnsureSuccessStatusCode();
        var access = (await created.Content.ReadFromJsonAsync<SubsonicAccessDto>(Ct))!;
        Assert.True(access.Enabled);
        Assert.Equal(20, access.Password!.Length);
        Assert.Equal("admin", access.Username);

        // Relu plus tard pour configurer un autre client : le même.
        var again = (await admin.GetFromJsonAsync<SubsonicAccessDto>("/api/me/subsonic", Ct))!;
        Assert.Equal(access.Password, again.Password);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/me/subsonic", Ct)).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<SubsonicAccessDto>("/api/me/subsonic", Ct))!.Enabled);
    }

    [Fact]
    public async Task The_subsonic_password_requires_a_session()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/subsonic", Ct)).StatusCode);
    }

    // ---- /rest/* ----

    private sealed record Session(HttpClient Client, string Password)
    {
        public string Token(string user = "admin")
        {
            var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var t = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Password + salt))).ToLowerInvariant();
            return $"u={user}&t={t}&s={salt}&v=1.16.1&c=tests";
        }

        public async Task<JsonElement> JsonAsync(string method, string query = "")
        {
            var text = await Client.GetStringAsync($"/rest/{method}?{Token()}&f=json{(query.Length > 0 ? "&" + query : "")}", Ct);
            return JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").Clone();
        }
    }

    private async Task<Session> SessionAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var access = (await (await admin.PostAsync("/api/me/subsonic", null, Ct)).Content.ReadFromJsonAsync<SubsonicAccessDto>(Ct))!;
        return new Session(factory.CreateClient(), access.Password!);
    }

    private static void AssertOk(JsonElement response) =>
        Assert.True(response.GetProperty("status").GetString() == "ok", response.ToString());

    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    private static async Task<int> CodeAsync(HttpClient c, string query) =>
        ErrorCode(JsonDocument.Parse(await c.GetStringAsync("/rest/ping?" + query + "&v=1.16.1&c=tests&f=json", Ct)).RootElement.GetProperty("subsonic-response"));

    [Fact]
    public async Task Clients_sign_in_with_a_token_a_password_or_an_api_key()
    {
        var s = await SessionAsync();
        var c = s.Client;

        AssertOk(await s.JsonAsync("ping"));
        Assert.Contains("\"ok\"", await c.GetStringAsync($"/rest/ping.view?u=admin&p={s.Password}&v=1.13.0&c=tests&f=json", Ct));
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(s.Password)).ToLowerInvariant();
        Assert.Contains("\"ok\"", await c.GetStringAsync($"/rest/ping?u=admin&p=enc:{hex}&v=1.13.0&c=tests&f=json", Ct));
        Assert.Contains("\"ok\"", await c.GetStringAsync($"/rest/ping?apiKey={s.Password}&v=1.16.1&c=tests&f=json", Ct));

        Assert.Equal(10, await CodeAsync(c, "u=admin"));
        Assert.Equal(40, await CodeAsync(c, "u=admin&p=wrong"));
        // Le mot de passe du compte ne vaut pas mot de passe Subsonic.
        Assert.Equal(40, await CodeAsync(c, $"u=admin&p={SonNuageFactory.AdminPassword}"));
        Assert.Equal(43, await CodeAsync(c, $"u=admin&apiKey={s.Password}"));
        Assert.Equal(44, await CodeAsync(c, "apiKey=nope"));
    }

    [Fact]
    public async Task Responses_are_xml_by_default_and_extensions_need_no_sign_in()
    {
        var s = await SessionAsync();

        var response = await s.Client.GetAsync($"/rest/getMusicFolders?{s.Token()}", Ct);
        Assert.StartsWith("text/xml", response.Content.Headers.ContentType!.ToString());
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ok", (string?)xml.Root!.Attribute("status"));

        var ext = await s.Client.GetStringAsync("/rest/getOpenSubsonicExtensions?f=json", Ct);
        Assert.Contains("apiKeyAuthentication", ext);
        Assert.Equal(0, ErrorCode(await s.JsonAsync("noSuchMethod")));
    }

    [Fact]
    public async Task Artists_albums_and_songs_can_be_browsed()
    {
        var s = await SessionAsync();

        var artists = (await s.JsonAsync("getArtists")).GetProperty("artists").GetProperty("index").EnumerateArray()
            .SelectMany(i => i.GetProperty("artist").EnumerateArray()).ToList();
        var artist = artists.Single(a => a.GetProperty("name").GetString() == "Test Artist");
        Assert.Equal(1, artist.GetProperty("albumCount").GetInt32());
        AssertOk(await s.JsonAsync("getIndexes"));

        var detail = (await s.JsonAsync("getArtist", "id=" + artist.GetProperty("id").GetString())).GetProperty("artist");
        var albumId = detail.GetProperty("album")[0].GetProperty("id").GetString();
        var album = (await s.JsonAsync("getAlbum", "id=" + albumId)).GetProperty("album");
        Assert.Equal("First Album", album.GetProperty("name").GetString());
        Assert.Equal(2, album.GetProperty("songCount").GetInt32());
        var songs = album.GetProperty("song").EnumerateArray().ToList();
        Assert.Equal(["Opening", "Second Song"], songs.Select(x => x.GetProperty("title").GetString()));
        Assert.Equal(2, songs[0].GetProperty("duration").GetInt32());
        Assert.Equal("wav", songs[0].GetProperty("suffix").GetString());
        Assert.False(string.IsNullOrEmpty(album.GetProperty("coverArt").GetString()));

        var song = (await s.JsonAsync("getSong", "id=" + songs[0].GetProperty("id").GetString())).GetProperty("song");
        Assert.Equal("Opening", song.GetProperty("title").GetString());
        var dir = (await s.JsonAsync("getMusicDirectory", "id=" + albumId)).GetProperty("directory");
        Assert.Equal(2, dir.GetProperty("child").GetArrayLength());
        Assert.Equal(70, ErrorCode(await s.JsonAsync("getAlbum", "id=al-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task Search_lists_and_genres_follow_the_library()
    {
        var s = await SessionAsync();

        var found = (await s.JsonAsync("search3", "query=second")).GetProperty("searchResult3");
        Assert.Contains(found.GetProperty("song").EnumerateArray(), x => x.GetProperty("title").GetString() == "Second Song");
        // Requête vide (synchronisation complète de Symfonium) : toute la bibliothèque, par pages.
        var all = (await s.JsonAsync("search3", "query=%22%22&songCount=500&artistCount=500&albumCount=500")).GetProperty("searchResult3");
        Assert.Equal(3, all.GetProperty("song").GetArrayLength());
        var page = (await s.JsonAsync("search3", "query=&songCount=2&songOffset=2")).GetProperty("searchResult3");
        Assert.Equal(1, page.GetProperty("song").GetArrayLength());

        var list = (await s.JsonAsync("getAlbumList2", "type=alphabeticalByName&size=10")).GetProperty("albumList2").GetProperty("album");
        Assert.Equal(["First Album", "Live"], list.EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        var random = (await s.JsonAsync("getRandomSongs", "size=2")).GetProperty("randomSongs").GetProperty("song");
        Assert.Equal(2, random.GetArrayLength());
        AssertOk(await s.JsonAsync("getGenres"));
        AssertOk(await s.JsonAsync("getAlbumList", "type=newest"));
    }

    [Fact]
    public async Task Songs_stream_with_covers_and_downloads()
    {
        var s = await SessionAsync();
        var album = (await s.JsonAsync("getAlbumList2", "type=alphabeticalByName")).GetProperty("albumList2").GetProperty("album")[0];
        var song = (await s.JsonAsync("getAlbum", "id=" + album.GetProperty("id").GetString())).GetProperty("album").GetProperty("song")[0];
        var id = song.GetProperty("id").GetString();

        var stream = await s.Client.GetAsync($"/rest/stream?{s.Token()}&id={id}&format=raw", Ct);
        stream.EnsureSuccessStatusCode();
        Assert.Equal("RIFF", Encoding.ASCII.GetString((await stream.Content.ReadAsByteArrayAsync(Ct))[..4]));
        var download = await s.Client.GetAsync($"/rest/download?{s.Token()}&id={id}", Ct);
        download.EnsureSuccessStatusCode();

        var cover = await s.Client.GetAsync($"/rest/getCoverArt?{s.Token()}&id={album.GetProperty("coverArt").GetString()}&size=300", Ct);
        cover.EnsureSuccessStatusCode();
        Assert.StartsWith("image/", cover.Content.Headers.ContentType!.ToString());
        // Mauvais identifiants : pas de flux.
        var denied = await s.Client.GetAsync($"/rest/stream?u=admin&p=wrong&v=1.16.1&c=tests&id={id}", Ct);
        Assert.Contains("failed", await denied.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Stars_playlists_and_scrobbles_belong_to_the_user()
    {
        var s = await SessionAsync();
        var album = (await s.JsonAsync("getAlbumList2", "type=alphabeticalByName")).GetProperty("albumList2").GetProperty("album")[0];
        var songs = (await s.JsonAsync("getAlbum", "id=" + album.GetProperty("id").GetString())).GetProperty("album").GetProperty("song");
        var first = songs[0].GetProperty("id").GetString();
        var second = songs[1].GetProperty("id").GetString();

        AssertOk(await s.JsonAsync("star", "id=" + first));
        var starred = (await s.JsonAsync("getStarred2")).GetProperty("starred2").GetProperty("song");
        Assert.Contains(starred.EnumerateArray(), x => x.GetProperty("id").GetString() == first);
        Assert.True((await s.JsonAsync("getSong", "id=" + first)).GetProperty("song").TryGetProperty("starred", out _));
        AssertOk(await s.JsonAsync("unstar", "id=" + first));
        Assert.DoesNotContain((await s.JsonAsync("getStarred2")).GetProperty("starred2").GetProperty("song").EnumerateArray(),
            x => x.GetProperty("id").GetString() == first);

        var created = (await s.JsonAsync("createPlaylist", $"name=Route&songId={first}&songId={second}")).GetProperty("playlist");
        var playlistId = created.GetProperty("id").GetString();
        Assert.Equal(2, created.GetProperty("songCount").GetInt32());
        AssertOk(await s.JsonAsync("updatePlaylist", $"playlistId={playlistId}&name=Route+66&songIndexToRemove=0&songIdToAdd={first}"));
        var playlist = (await s.JsonAsync("getPlaylist", "id=" + playlistId)).GetProperty("playlist");
        Assert.Equal("Route 66", playlist.GetProperty("name").GetString());
        Assert.Equal([second, first], playlist.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Contains((await s.JsonAsync("getPlaylists")).GetProperty("playlists").GetProperty("playlist").EnumerateArray(),
            p => p.GetProperty("id").GetString() == playlistId);
        AssertOk(await s.JsonAsync("deletePlaylist", "id=" + playlistId));
        Assert.Equal(70, ErrorCode(await s.JsonAsync("getPlaylist", "id=" + playlistId)));

        AssertOk(await s.JsonAsync("scrobble", $"id={second}&submission=true"));
        var admin = await factory.AdminClientAsync();
        var recent = (await admin.GetFromJsonAsync<List<TrackDto>>("/api/me/recent", Ct))!;
        Assert.Equal("Second Song", recent[0].Title);
    }

    [Fact]
    public async Task Post_forms_are_accepted()
    {
        var s = await SessionAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = "admin", ["p"] = s.Password, ["v"] = "1.16.1", ["c"] = "tests", ["f"] = "json",
        });
        var response = await s.Client.PostAsync("/rest/ping.view", form, Ct);
        Assert.Contains("\"ok\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Clients_that_ask_for_unsupported_features_get_valid_empty_answers()
    {
        var s = await SessionAsync();
        foreach (var method in new[] { "getPodcasts", "getInternetRadioStations", "getShares", "getBookmarks", "getNowPlaying",
                     "getScanStatus", "getUser&username=admin", "getLicense", "getArtistInfo2&id=x", "getAlbumInfo2&id=x",
                     "getTopSongs&artist=Test%20Artist", "getLyrics&artist=a&title=b" })
        {
            var parts = method.Split('&', 2);
            AssertOk(await s.JsonAsync(parts[0], parts.Length > 1 ? parts[1] : ""));
        }
    }
}
