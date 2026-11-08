using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Versions affichées (aide, menu du compte, réglages) : celle de la publication, sans les métadonnées de build.</summary>
public sealed class AppVersionTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    [Theory]
    [InlineData("1.5.1", "1.5.1")]
    [InlineData("1.5.1+524e672c0ffee", "1.5.1")]
    [InlineData("0.1.0-ci.37+abc", "0.1.0-ci.37")]
    [InlineData("  2.0.0  ", "2.0.0")]
    public void Build_metadata_is_dropped(string informational, string expected) =>
        Assert.Equal(expected, AppVersion.Clean(informational));

    [Fact]
    public void An_assembly_gives_its_published_version()
    {
        var expected = AppVersion.Clean(typeof(AppVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion);
        Assert.Equal(expected, AppVersion.Of(typeof(AppVersion).Assembly));
    }

    [Fact]
    public async Task The_server_reports_its_published_version()
    {
        var info = await factory.CreateClient().GetFromJsonAsync<ServerInfoDto>("/api/info", TestContext.Current.CancellationToken);
        Assert.Equal(AppVersion.Of(typeof(SonNuage.Server.Api.LibraryEndpoints).Assembly), info!.Version);
    }
}
