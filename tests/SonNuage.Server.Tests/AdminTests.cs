using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Comptes, albums et pochettes depuis l'admin, comme les utilise l'interface.</summary>
public sealed class AdminTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UserDto> CreateUserAsync(HttpClient admin, string name, string role = Roles.User)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, "password-1234", role), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>(Ct))!;
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_and_roles_and_passwords_are_checked()
    {
        var admin = await factory.AdminClientAsync();
        var users = (await admin.GetFromJsonAsync<List<UserDto>>("/api/admin/users", Ct))!;
        var me = users.Single(u => u.Username == "admin");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(null, Roles.User), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(null, "root"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}", new UpdateUserRequest(null, Roles.User), Ct)).StatusCode);

        var bob = await CreateUserAsync(admin, "bob");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/admin/users/{bob.Id}", new UpdateUserRequest("court", null), Ct)).StatusCode);

        // Promu, bob est un second admin : le premier peut alors redevenir simple utilisateur… puis être repromu.
        var promoted = await admin.PatchAsJsonAsync($"/api/admin/users/{bob.Id}", new UpdateUserRequest("nouveau-mot-de-passe", Roles.Admin), Ct);
        Assert.Equal(Roles.Admin, (await promoted.Content.ReadFromJsonAsync<UserDto>(Ct))!.Role);
        var bobClient = await factory.ClientAsync("bob", "nouveau-mot-de-passe");
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PatchAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(null, Roles.User), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PatchAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(null, Roles.Admin), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await bobClient.DeleteAsync($"/api/admin/users/{bob.Id}", Ct)).StatusCode);
        var adminAgain = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await adminAgain.DeleteAsync($"/api/admin/users/{bob.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminAgain.DeleteAsync($"/api/admin/users/{bob.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Renaming_an_album_moves_its_tracks_and_sets_its_year()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var albums = await admin.GetFromJsonAsync<PagedResult<AlbumDto>>("/api/albums?sort=title&limit=200", Ct);
        var live = albums!.Items.Single(a => a.Title == "Live");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/albums/{live.Id}", new UpdateAlbumRequest(" ", null, null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/admin/albums/{Guid.NewGuid()}", new UpdateAlbumRequest("X", null, null), Ct)).StatusCode);

        var response = await admin.PutAsJsonAsync($"/api/admin/albums/{live.Id}", new UpdateAlbumRequest("Live à Paris", "Other Band", 1999), Ct);
        var renamed = (await response.Content.ReadFromJsonAsync<AlbumDto>(Ct))!;
        Assert.Equal("Live à Paris", renamed.Title);
        Assert.Equal(1999, renamed.Year);
        Assert.Equal(1, renamed.TrackCount);
        var tracks = await admin.GetFromJsonAsync<PagedResult<TrackMetadataDto>>($"/api/admin/tracks?albumId={renamed.Id}", Ct);
        var track = Assert.Single(tracks!.Items);
        Assert.True(track.MetadataLocked);
        Assert.Equal("Live à Paris", track.AlbumTitle);
    }

    [Fact]
    public async Task A_cover_must_be_a_jpeg_png_or_webp_image_of_reasonable_size()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var album = (await admin.GetFromJsonAsync<PagedResult<AlbumDto>>("/api/albums?sort=title&limit=200", Ct))!.Items.First();

        async Task<HttpStatusCode> PutAsync(Guid id, byte[] body, string type)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return (await admin.PutAsync($"/api/admin/albums/{id}/cover", content, Ct)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await PutAsync(album.Id, [1, 2, 3], "image/gif"));
        Assert.Equal(HttpStatusCode.BadRequest, await PutAsync(album.Id, [], "image/png"));
        Assert.Equal(HttpStatusCode.NotFound, await PutAsync(Guid.NewGuid(), [0x89, 0x50, 0x4E, 0x47], "image/png"));
        Assert.Equal(HttpStatusCode.NoContent, await PutAsync(album.Id, [0x89, 0x50, 0x4E, 0x47], "image/png"));

        var after = await admin.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{album.Id}", Ct);
        Assert.True(after!.Album.HasCover);
    }
}
