using System.Net;
using System.Net.Http.Json;
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
}
