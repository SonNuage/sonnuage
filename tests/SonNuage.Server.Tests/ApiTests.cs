using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using SonNuage.Contracts;
using SonNuage.Server.Streaming;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.Server.Tests;

/// <summary>
/// Serveur complet sur une bibliothèque temporaire de WAV générés (pas de fichiers binaires dans le dépôt).
/// </summary>
public class SonNuageFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-admin-password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnuage-tests-" + Guid.NewGuid().ToString("N"));

    public string MusicPath => Path.Combine(_root, "music");
    public string UploadPath => Path.Combine(_root, "uploads");
    public string DataPath => Path.Combine(_root, "data");

    public SonNuageFactory()
    {
        // Artiste/Album/NN Titre.wav, sans tags : tout est déduit de l'arborescence.
        WriteWav(Path.Combine(MusicPath, "Test Artist", "First Album", "01 - Opening.wav"), seconds: 2);
        WriteWav(Path.Combine(MusicPath, "Test Artist", "First Album", "02_Second_Song.wav"), seconds: 3);
        WriteWav(Path.Combine(MusicPath, "Test Artist", "First Album", "02_Second_Song (1).wav"), seconds: 3);
        WriteWav(Path.Combine(MusicPath, "Other Band", "Live", "Encore.wav"), seconds: 1);
        File.WriteAllBytes(Path.Combine(MusicPath, "Test Artist", "First Album", "folder.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("SonNuage:MusicPaths:0", MusicPath);
        builder.UseSetting("SonNuage:UploadPath", UploadPath);
        builder.UseSetting("SonNuage:DataPath", DataPath);
        builder.UseSetting("SonNuage:InitialAdminUser", "admin");
        builder.UseSetting("SonNuage:InitialAdminPassword", AdminPassword);
        builder.UseSetting("SonNuage:WatchFileSystem", "false");
        builder.UseSetting("SonNuage:BehindProxy", "false");
        builder.UseSetting("SonNuage:LoginAttemptsPerMinute", "1000");
        builder.UseSetting("SonNuage:FfmpegPath", Environment.GetEnvironmentVariable("SONNUAGE_TEST_FFMPEG") ?? "ffmpeg");
        ConfigureMore(builder);
    }

    protected virtual void ConfigureMore(IWebHostBuilder builder)
    {
    }

    public static void WriteWav(string path, int seconds, int sampleRate = 8000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var samples = seconds * sampleRate;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / sampleRate) * 8000));
    }

    public async Task<HttpClient> AdminClientAsync() => await ClientAsync("admin", AdminPassword);

    public async Task<HttpClient> ClientAsync(string user, string password)
    {
        var client = CreateClient();
        var tokens = await LoginAsync(client, user, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public static async Task<TokenResponse> LoginAsync(HttpClient client, string user, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, password, "tests"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    /// <summary>Le scan initial tourne en tâche de fond : on attend qu'il ait fini.</summary>
    public async Task WaitForScanAsync(HttpClient admin)
    {
        for (var i = 0; i < 100; i++)
        {
            var stats = await admin.GetFromJsonAsync<LibraryStatsDto>("/api/admin/stats");
            if (stats!.Scan is { Running: false, LastFinishedAt: not null }) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Scan initial trop long");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}

public sealed class ApiTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Library_requires_authentication()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/albums", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/info", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz", Ct)).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "nope", null), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scan_groups_folders_into_artists_and_albums_and_skips_windows_copies()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);

        var albums = await admin.GetFromJsonAsync<PagedResult<AlbumDto>>("/api/albums?sort=title", Ct);
        var first = Assert.Single(albums!.Items, a => a.Title == "First Album");
        Assert.Equal("Test Artist", first.ArtistName);
        Assert.True(first.HasCover);

        var detail = await admin.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{first.Id}", Ct);
        Assert.Equal(["Opening", "Second Song"], detail!.Tracks.Select(t => t.Title));
        Assert.Equal([1, 2], detail.Tracks.Select(t => t.TrackNumber ?? 0));
        Assert.InRange(detail.Tracks[0].DurationSeconds, 1.9, 2.1);
    }

    [Fact]
    public async Task Search_ignores_case_and_accents()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var result = await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=SÉCOND", Ct);
        Assert.Contains(result!.Tracks, t => t.Title == "Second Song");
    }

    [Fact]
    public async Task Stream_supports_range_and_media_token_in_query()
    {
        var anonymous = factory.CreateClient();
        var tokens = await SonNuageFactory.LoginAsync(anonymous, "admin", SonNuageFactory.AdminPassword);
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var track = (await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=opening", Ct))!.Tracks[0];

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tracks/{track.Id}/stream?quality=original&t={tokens.MediaToken}");
        request.Headers.Range = new RangeHeaderValue(0, 43);
        var response = await anonymous.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(44, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);

        // Le jeton d'API n'est pas accepté en query, et le jeton média ne donne pas accès à l'API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/tracks/{track.Id}/stream?t={tokens.AccessToken}", Ct)).StatusCode);
        var withMediaHeader = new HttpRequestMessage(HttpMethod.Get, "/api/albums");
        withMediaHeader.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.MediaToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(withMediaHeader, Ct)).StatusCode);
    }

    [Fact]
    public async Task Transcoded_stream_is_mp3_and_then_served_from_cache_with_range()
    {
        var transcoder = factory.Services.GetRequiredService<TranscodeService>();
        Assert.SkipUnless(transcoder.Available, "ffmpeg absent");

        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var track = (await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=encore", Ct))!.Tracks[0];

        var first = await admin.GetAsync($"/api/tracks/{track.Id}/stream?quality=low", Ct);
        first.EnsureSuccessStatusCode();
        Assert.Equal("audio/mpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("low", first.Headers.GetValues("X-SonNuage-Quality").Single());
        var body = await first.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(body.Length > 1000);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tracks/{track.Id}/stream?quality=low");
        request.Headers.Range = new RangeHeaderValue(100, null);
        var ranged = await admin.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(body.Length - 100, (await ranged.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task Playlists_keep_order_and_support_moves()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var album = (await admin.GetFromJsonAsync<PagedResult<AlbumDto>>("/api/albums?q=first", Ct))!.Items[0];
        var tracks = (await admin.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{album.Id}", Ct))!.Tracks;

        var created = await admin.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest("Mix", null, [tracks[0].Id, tracks[1].Id]), Ct);
        var playlist = (await created.Content.ReadFromJsonAsync<PlaylistDto>(Ct))!;
        Assert.Equal(2, playlist.TrackCount);

        await admin.PostAsJsonAsync($"/api/playlists/{playlist.Id}/tracks", new AddPlaylistTracksRequest([tracks[0].Id], 0), Ct);
        var detail = (await admin.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist.Id}", Ct))!;
        Assert.Equal([tracks[0].Id, tracks[0].Id, tracks[1].Id], detail.Entries.Select(e => e.Track.Id));

        await admin.PostAsJsonAsync($"/api/playlists/{playlist.Id}/entries/{detail.Entries[2].EntryId}/move", new MovePlaylistEntryRequest(0), Ct);
        detail = (await admin.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist.Id}", Ct))!;
        Assert.Equal([tracks[1].Id, tracks[0].Id, tracks[0].Id], detail.Entries.Select(e => e.Track.Id));

        await admin.DeleteAsync($"/api/playlists/{playlist.Id}/entries/{detail.Entries[1].EntryId}", Ct);
        detail = (await admin.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist.Id}", Ct))!;
        Assert.Equal([0, 1], detail.Entries.Select(e => e.Position));
    }

    [Fact]
    public async Task Regular_users_cannot_reach_admin_endpoints_or_others_playlists()
    {
        var admin = await factory.AdminClientAsync();
        var create = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("alice", "alice-password", Roles.User), Ct);
        create.EnsureSuccessStatusCode();
        var adminPlaylist = (await (await admin.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest("Privée", null, null), Ct))
            .Content.ReadFromJsonAsync<PlaylistDto>(Ct))!;

        var alice = await factory.ClientAsync("alice", "alice-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/admin/stats", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/playlists/{adminPlaylist.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_session()
    {
        var client = factory.CreateClient();
        var first = await SonNuageFactory.LoginAsync(client, "admin", SonNuageFactory.AdminPassword);

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Ct);
        refreshed.EnsureSuccessStatusCode();
        var second = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;

        // Rejoué aussitôt (deux onglets qui renouvellent en même temps) : refusé, sans toucher à la session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Ct)).StatusCode);
        var again = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(second.RefreshToken), Ct);
        again.EnsureSuccessStatusCode();
        var third = (await again.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;

        // Rejoué plus tard (vol présumé) : toute la lignée est révoquée, y compris le jeton courant.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuage.Server.Data.SonNuageDbContext>();
            foreach (var token in db.RefreshTokens.Where(r => r.RevokedAt != null).ToList()) token.RevokedAt = token.RevokedAt!.Value.AddMinutes(-5);
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(third.RefreshToken), Ct)).StatusCode);
    }

    [Fact]
    public async Task Chunked_upload_lands_in_library_sorted_by_hints()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        SonNuageFactory.WriteWav(source, seconds: 2);
        var bytes = await File.ReadAllBytesAsync(source, Ct);
        File.Delete(source);

        var start = await admin.PostAsJsonAsync("/api/admin/uploads", new StartUploadRequest("Nouveau titre.wav", bytes.Length, "Uploader", "Envois"), Ct);
        var session = (await start.Content.ReadFromJsonAsync<UploadSessionDto>(Ct))!;

        // Deux morceaux, comme le ferait le client avec des morceaux de 8 Mo.
        var half = bytes.Length / 2;
        var put1 = await admin.PutAsync($"/api/admin/uploads/{session.Id}?offset=0", new ByteArrayContent(bytes[..half]), Ct);
        put1.EnsureSuccessStatusCode();
        // Mauvais offset : refusé avec l'offset attendu.
        var wrong = await admin.PutAsync($"/api/admin/uploads/{session.Id}?offset=0", new ByteArrayContent(bytes[half..]), Ct);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        var put2 = await admin.PutAsync($"/api/admin/uploads/{session.Id}?offset={half}", new ByteArrayContent(bytes[half..]), Ct);
        put2.EnsureSuccessStatusCode();

        var complete = await admin.PostAsync($"/api/admin/uploads/{session.Id}/complete", null, Ct);
        complete.EnsureSuccessStatusCode();
        var track = Assert.Single((await complete.Content.ReadFromJsonAsync<List<TrackDto>>(Ct))!);
        Assert.Equal("Uploader", track.ArtistName);
        Assert.Equal("Envois", track.AlbumTitle);
        Assert.Equal("Nouveau titre", track.Title);
    }
}
