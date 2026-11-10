using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>
/// L'appli de bureau (Photino) sert ses pages depuis http://localhost (WebView2, Windows) ou app://localhost (WebKit,
/// macOS et Linux) : son pré-chargement par fetch lit le flux et la pochette en cross-origin. Seules ces origines y
/// ont droit, sans cookies (le jeton média est dans l'adresse), et rien d'autre de l'API ne s'ouvre.
/// </summary>
public sealed class DesktopCorsTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("app://localhost")]
    public async Task Stream_preflight_from_the_desktop_app_is_allowed(string origin)
    {
        var response = await factory.CreateClient().SendAsync(Preflight($"/api/tracks/{Guid.NewGuid()}/stream?quality=high", origin, "range"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("GET", Header(response, "Access-Control-Allow-Methods"));
        Assert.Contains("range", Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Stream_from_the_desktop_app_carries_cors_headers()
    {
        var (anonymous, track, media) = await TrackAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tracks/{track.Id}/stream?quality=original&t={media}");
        request.Headers.Add("Origin", "app://localhost");
        var response = await anonymous.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("app://localhost", Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("X-SonNuage-Duration", Header(response, "Access-Control-Expose-Headers"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", response.Headers.Vary);
    }

    [Fact]
    public async Task Cover_from_the_desktop_app_carries_cors_headers()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var album = (await admin.GetFromJsonAsync<PagedResult<AlbumDto>>("/api/albums?sort=title", Ct))!.Items.Single(a => a.Title == "First Album");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/albums/{album.Id}/cover");
        request.Headers.Add("Origin", "http://localhost");
        var response = await admin.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://localhost", Header(response, "Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("null")]
    public async Task Other_origins_get_no_cors_header(string origin)
    {
        var (anonymous, track, media) = await TrackAsync();

        var preflight = await anonymous.SendAsync(Preflight($"/api/tracks/{track.Id}/stream", origin, "range"), Ct);
        Assert.Null(Header(preflight, "Access-Control-Allow-Origin"));

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tracks/{track.Id}/stream?quality=original&t={media}");
        request.Headers.Add("Origin", origin);
        var response = await anonymous.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task The_rest_of_the_api_stays_closed_to_the_desktop_origin()
    {
        var admin = await factory.AdminClientAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/albums");
        request.Headers.Add("Origin", "http://localhost");
        var response = await admin.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Header(response, "Access-Control-Allow-Origin"));

        var preflight = await admin.SendAsync(Preflight("/api/albums", "http://localhost", "authorization"), Ct);
        Assert.Null(Header(preflight, "Access-Control-Allow-Origin"));
    }

    private async Task<(HttpClient Anonymous, TrackDto Track, string MediaToken)> TrackAsync()
    {
        var anonymous = factory.CreateClient();
        var tokens = await SonNuageFactory.LoginAsync(anonymous, "admin", SonNuageFactory.AdminPassword);
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var track = (await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=opening", Ct))!.Tracks[0];
        return (anonymous, track, tokens.MediaToken);
    }

    private static HttpRequestMessage Preflight(string url, string origin, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
