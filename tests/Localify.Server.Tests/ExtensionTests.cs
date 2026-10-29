using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Localify.Contracts;

namespace Localify.Server.Tests;

/// <summary>Une clé d'extension n'ouvre que l'ajout d'imports YouTube, jamais le reste de l'API.</summary>
public sealed class ExtensionKeyTests(LocalifyFactory factory) : IClassFixture<LocalifyFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Admin, CreatedExtensionKeyDto Key)> CreateKeyAsync(string name = "tests")
    {
        var admin = await factory.AdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/admin/extension-keys", new CreateExtensionKeyRequest(name), Ct);
        response.EnsureSuccessStatusCode();
        return (admin, (await response.Content.ReadFromJsonAsync<CreatedExtensionKeyDto>(Ct))!);
    }

    private HttpClient KeyClient(string secret)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", secret);
        return client;
    }

    [Fact]
    public async Task Key_is_shown_once_and_stored_hashed()
    {
        var (admin, created) = await CreateKeyAsync("hash");
        Assert.StartsWith("lfy_ext_", created.Secret);
        Assert.StartsWith(created.Key.Prefix, created.Secret);
        var keys = await admin.GetFromJsonAsync<List<ExtensionKeyDto>>("/api/admin/extension-keys", Ct);
        Assert.Contains(keys!, k => k.Id == created.Key.Id && k.CreatedBy == "admin");
        Assert.DoesNotContain(created.Secret, await admin.GetStringAsync("/api/admin/extension-keys", Ct));
    }

    [Fact]
    public async Task Key_reaches_only_the_extension_endpoints()
    {
        var (_, created) = await CreateKeyAsync();
        var client = KeyClient(created.Secret);

        // Autorisées : la réponse vient du point d'accès lui-même (400 = validation, pas un refus d'accès).
        var status = await client.GetAsync("/api/extension/status?videoId=dQw4w9WgXcQ&url=https://www.youtube.com/playlist?list=PLx", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var dto = await status.Content.ReadFromJsonAsync<ExtensionStatusDto>(Ct);
        Assert.Null(dto!.Status);
        Assert.False(dto.Subscribed);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/youtube/imports", new YoutubeImportRequest([], null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/youtube/resolve?url=https://example.com/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/youtube/subscriptions", new CreateSubscriptionRequest("https://example.com/", false, false), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/youtube/cookies", new YoutubeCookiesRequest(""), Ct)).StatusCode);

        // Interdites : tout le reste de l'administration et la bibliothèque.
        string[] gets =
        [
            "/api/admin/youtube/status", "/api/admin/youtube/items", "/api/admin/youtube/settings", "/api/admin/youtube/subscriptions",
            "/api/admin/youtube/search?q=x", "/api/admin/extension-keys", "/api/admin/stats", "/api/admin/users", "/api/albums", "/api/me/favorites",
        ];
        foreach (var url in gets)
        {
            var code = (await client.GetAsync(url, Ct)).StatusCode;
            Assert.True(code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{url} : {code}");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/admin/youtube/cookies", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/extension-keys", new CreateExtensionKeyRequest("x"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/admin/youtube/settings", new YoutubeSettingsDto(1, "mp3", null, 0.8), Ct)).StatusCode);
    }

    [Fact]
    public async Task Import_through_key_is_visible_in_status()
    {
        var (_, created) = await CreateKeyAsync("import");
        var client = KeyClient(created.Secret);
        var video = new YoutubeVideoDto("aaaaaaaaaaa", "Artiste - Titre", "Chaîne", null, null, null);
        var response = await client.PostAsJsonAsync("/api/admin/youtube/imports", new YoutubeImportRequest([video], null), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await client.GetFromJsonAsync<ExtensionStatusDto>("/api/extension/status?videoId=aaaaaaaaaaa", Ct);
        Assert.NotNull(dto!.Status);
    }

    [Fact]
    public async Task Unknown_or_revoked_key_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient("lfy_ext_nope").GetAsync("/api/extension/status?videoId=dQw4w9WgXcQ", Ct)).StatusCode);

        var (admin, created) = await CreateKeyAsync("revoked");
        var client = KeyClient(created.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/extension/status?videoId=dQw4w9WgXcQ", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/extension-keys/{created.Key.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/extension/status?videoId=dQw4w9WgXcQ", Ct)).StatusCode);
    }

    [Fact]
    public async Task Regular_user_cannot_use_extension_endpoints()
    {
        var admin = await factory.AdminClientAsync();
        await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("ext-user", "password-123", Roles.User), Ct);
        var user = await factory.ClientAsync("ext-user", "password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/extension/status?videoId=dQw4w9WgXcQ", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/youtube/imports", new YoutubeImportRequest([], null), Ct)).StatusCode);
    }
}
