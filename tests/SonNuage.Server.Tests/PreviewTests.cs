using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Extrait d'une trentaine de secondes avant de demander un morceau : contributeurs et admins seulement.</summary>
public sealed class PreviewTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, TokenResponse Tokens)> AccountAsync(string name, string role)
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", role), Ct)).EnsureSuccessStatusCode();
        var client = factory.CreateClient();
        var tokens = await SonNuageFactory.LoginAsync(client, name, $"{name}-password");
        return (client, tokens);
    }

    private string Cached(string videoId) => Path.Combine(factory.DataPath, "cache", "previews", $"{videoId}.mp3");

    [Fact]
    public async Task Only_contributors_and_admins_hear_a_preview()
    {
        var anonymous = factory.CreateClient();
        var (user, userTokens) = await AccountAsync("prev-regular", Roles.User);
        var (contributor, tokens) = await AccountAsync("prev-contrib", Roles.Contributor);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/requests/preview/cccccccccc1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/requests/preview/cccccccccc1?t={userTokens.MediaToken}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await contributor.GetAsync($"/api/requests/preview/too-short?t={tokens.MediaToken}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_cached_preview_is_served_with_ranges_through_the_media_token()
    {
        var (contributor, tokens) = await AccountAsync("prev-cached", Roles.Contributor);
        Directory.CreateDirectory(Path.GetDirectoryName(Cached("cccccccccc2"))!);
        await File.WriteAllBytesAsync(Cached("cccccccccc2"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray(), Ct);

        var full = await contributor.GetAsync($"/api/requests/preview/cccccccccc2?d=213&t={tokens.MediaToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("audio/mpeg", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4096, (await full.Content.ReadAsByteArrayAsync(Ct)).Length);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/requests/preview/cccccccccc2?t={tokens.MediaToken}");
        ranged.Headers.Range = new RangeHeaderValue(100, 199);
        var part = await contributor.SendAsync(ranged, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(100, (await part.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task Without_yt_dlp_a_preview_fails_cleanly()
    {
        var admin = await factory.AdminClientAsync();
        var response = await admin.GetAsync("/api/requests/preview/cccccccccc3", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(File.Exists(Cached("cccccccccc3")));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(20.0, 0)]
    [InlineData(59.0, 0)]
    [InlineData(100.0, 30)]
    [InlineData(213.0, 64)]
    [InlineData(600.0, 180)]
    public void A_preview_starts_about_a_third_into_the_song(double? duration, int expected) =>
        Assert.Equal(expected, PreviewService.StartFor(duration));
}
