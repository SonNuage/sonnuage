using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;
using SonNuage.Server.Jobs;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>
/// Faux MusicBrainz, ListenBrainz et Cover Art Archive derrière le client HTTP « musicbrainz » : « Test Artist » est
/// identifié par la fiche de son album, « Other Band » par l'admin. Les dates sont relatives à aujourd'hui.
/// </summary>
public sealed class ReleasesFactory : SonNuageFactory
{
    public static readonly Guid TestArtistMbid = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid OtherBandMbid = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public static readonly Guid FirstAlbumRelease = Guid.Parse("33333333-3333-4333-8333-333333333333");
    public static readonly Guid FirstAlbumGroup = Guid.Parse("44444444-4444-4444-8444-444444444401");
    public static readonly Guid NewAlbumGroup = Guid.Parse("44444444-4444-4444-8444-444444444402");
    public static readonly Guid NextSingleGroup = Guid.Parse("44444444-4444-4444-8444-444444444403");
    public static readonly Guid OldLiveGroup = Guid.Parse("44444444-4444-4444-8444-444444444404");
    public static readonly Guid AncientEpGroup = Guid.Parse("44444444-4444-4444-8444-444444444405");
    public static readonly Guid FreshEpGroup = Guid.Parse("44444444-4444-4444-8444-444444444406");
    public static readonly Guid LiveGroup = Guid.Parse("44444444-4444-4444-8444-444444444407");

    /// <summary>Requêtes reçues par les fausses bases, dans l'ordre.</summary>
    public readonly ConcurrentQueue<Uri> Requests = new();

    private readonly SemaphoreSlim _setup = new(1, 1);
    private bool _ready;

    public ReleasesFactory()
    {
        MusicBrainzClient.Spacing = TimeSpan.FromMilliseconds(10);
        JobEngine.PollInterval = TimeSpan.FromMilliseconds(200);
    }

    protected override void ConfigureMore(IWebHostBuilder builder)
    {
        builder.UseSetting("SonNuage:MusicBrainzUrl", "http://mb.test");
        builder.UseSetting("SonNuage:ListenBrainzUrl", "http://lb.test");
        builder.UseSetting("SonNuage:CoverArtArchiveUrl", "http://caa.test");
        builder.UseSetting("SonNuage:DeezerUrl", "http://dz.test");
        builder.ConfigureServices(s => s.AddHttpClient(MusicBrainzClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Fake(this)));
    }

    public int CountRequests(string host, string path) => Requests.Count(u => u.Host == host && u.AbsolutePath.StartsWith(path, StringComparison.Ordinal));

    /// <summary>Une seule fois pour toute la classe : identifie les artistes et lit leurs parutions (tâche des nouveautés).</summary>
    public async Task<HttpClient> ReadyAsync()
    {
        var admin = await AdminClientAsync();
        await _setup.WaitAsync();
        try
        {
            if (_ready) return admin;
            await WaitForScanAsync(admin);
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                await db.Albums.Where(a => a.Title == "First Album").ExecuteUpdateAsync(s => s.SetProperty(a => a.MusicBrainzReleaseId, FirstAlbumRelease));
            }
            var otherBand = (await admin.GetFromJsonAsync<PagedResult<ArtistDto>>("/api/artists?q=other"))!.Items.Single();
            (await admin.PutAsJsonAsync($"/api/admin/artists/{otherBand.Id}/musicbrainz", new LinkArtistRequest(OtherBandMbid.ToString()))).EnsureSuccessStatusCode();
            await RunReleasesJobAsync(admin);
            _ready = true;
            return admin;
        }
        finally
        {
            _setup.Release();
        }
    }

    public async Task RunReleasesJobAsync(HttpClient admin)
    {
        var plan = ArtistReleasesJob.Plan();
        using (var scope = Services.CreateScope())
            await Services.GetRequiredService<JobEngine>().EnqueueAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), plan, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = (await admin.GetFromJsonAsync<JobDetailDto>($"/api/admin/jobs/{plan.Last.Id}"))!.Job;
            if (job.Status == "Succeeded") return;
            if (job.Status is "Failed" or "Canceled" || watch.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception($"{job.Title} : {job.Status}");
            await Task.Delay(50);
        }
    }

    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");

    private static string Group(Guid id, string title, string type, string date, params string[] secondary) =>
        $$"""{"id":"{{id}}","title":"{{title}}","primary-type":"{{type}}","secondary-types":[{{string.Join(",", secondary.Select(s => $"\"{s}\""))}}],"first-release-date":"{{date}}"}""";

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        var query = Uri.UnescapeDataString(uri.Query);
        string? json = (uri.Host, uri.AbsolutePath) switch
        {
            ("mb.test", var p) when p == $"/ws/2/release/{FirstAlbumRelease}" =>
                $$$"""{"id":"{{{FirstAlbumRelease}}}","title":"First Album","release-group":{"id":"{{{FirstAlbumGroup}}}","primary-type":"Album"},"artist-credit":[{"name":"Test Artist","joinphrase":"","artist":{"id":"{{{TestArtistMbid}}}","name":"Test Artist"}}]}""",
            ("mb.test", "/ws/2/release-group") when query.Contains($"artist={TestArtistMbid}") =>
                $$"""{"release-group-count":5,"release-groups":[{{string.Join(",",
                    Group(FirstAlbumGroup, "First Album", "Album", "2001-03-12"),
                    Group(NewAlbumGroup, "New Album", "Album", Day(-10)),
                    Group(NextSingleGroup, "Next Single", "Single", Day(20)),
                    Group(OldLiveGroup, "Old Live", "Album", Day(-5), "Live"),
                    Group(AncientEpGroup, "Ancient EP", "EP", "2010"))}}]}""",
            ("mb.test", "/ws/2/release-group") when query.Contains($"artist={OtherBandMbid}") =>
                $$"""{"release-group-count":1,"release-groups":[{{Group(LiveGroup, "Live", "Album", Day(-5))}}]}""",
            ("lb.test", "/1/explore/fresh-releases/") =>
                $$$"""{"payload":{"releases":[{"artist_credit_name":"Test Artist","artist_mbids":["{{{TestArtistMbid}}}"],"release_date":"{{{Day(-3)}}}","release_group_mbid":"{{{FreshEpGroup}}}","release_group_primary_type":"EP","release_group_secondary_type":null,"release_name":"Fresh EP"},{"artist_credit_name":"Inconnu","artist_mbids":["99999999-9999-4999-8999-999999999999"],"release_date":"{{{Day(-1)}}}","release_group_mbid":"{{{Guid.NewGuid()}}}","release_group_primary_type":"Album","release_name":"Ailleurs"}]}}""",
            // Deezer : « New Album » de « Test Artist » et ses deux titres ; rien pour les autres parutions.
            ("dz.test", "/search/album") when query.Contains("New Album") =>
                """{"data":[{"id":77,"title":"New Album","artist":{"name":"Test Artist"}}]}""",
            ("dz.test", "/search/album") => """{"data":[]}""",
            ("dz.test", "/album/77/tracks") =>
                """{"data":[{"id":1,"title":"Opening Song","duration":200},{"id":2,"title":"Second Song","duration":181}]}""",
            _ => null,
        };
        if (uri.Host == "caa.test" && uri.AbsolutePath == $"/release-group/{NewAlbumGroup}/front-250")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) };
        return json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Fake(ReleasesFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(owner.Respond(request));
    }
}

public sealed class ArtistReleasesTests(ReleasesFactory factory) : IClassFixture<ReleasesFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> WithDbAsync<T>(Func<SonNuageDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>());
    }

    private async Task<HttpClient> AccountAsync(HttpClient admin, string name, string role)
    {
        var created = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, "password-1234", role), Ct);
        Assert.True(created.IsSuccessStatusCode || created.StatusCode == HttpStatusCode.Conflict);
        return await factory.ClientAsync(name, "password-1234");
    }

    private static async Task<List<ArtistReleaseDto>> ReleasesAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<List<ArtistReleaseDto>>($"/api/releases{query}", Ct))!;

    private Task<Guid> ArtistIdAsync(string name) => WithDbAsync(db => db.Artists.Where(a => a.Name == name).Select(a => a.Id).SingleAsync(Ct));

    [Fact]
    public async Task The_refresh_derives_artist_ids_and_reads_release_groups_once_until_they_expire()
    {
        var admin = await factory.ReadyAsync();

        var artist = await WithDbAsync(db => db.Artists.SingleAsync(a => a.Name == "Test Artist", Ct));
        Assert.Equal((ReleasesFactory.TestArtistMbid.ToString(), ArtistMbidSource.Derived), (artist.MusicBrainzId, artist.MusicBrainzIdSource));
        Assert.Equal(ReleasesFactory.FirstAlbumGroup,
            await WithDbAsync(db => db.Albums.Where(a => a.Title == "First Album").Select(a => a.MusicBrainzReleaseGroupId).SingleAsync(Ct)));
        var fetches = await WithDbAsync(db => db.ExternalFetches.ToListAsync(Ct));
        Assert.All(fetches.Where(f => f.Key.StartsWith(ArtistReleasesJob.ReleaseGroupsPrefix, StringComparison.Ordinal)),
            f => Assert.True(f.ExpiresAt > DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Contains(fetches, f => f.Key == ArtistReleasesJob.FreshReleasesKey && f.ExpiresAt > DateTimeOffset.UtcNow.AddHours(23));
        Assert.Contains(fetches, f => f.Key.EndsWith(ReleasesFactory.TestArtistMbid.ToString(), StringComparison.Ordinal));
        Assert.Contains(fetches, f => f.Key.EndsWith(ReleasesFactory.OtherBandMbid.ToString(), StringComparison.Ordinal));

        var browsed = factory.CountRequests("mb.test", "/ws/2/release-group");
        var lookedUp = factory.CountRequests("mb.test", "/ws/2/release/");
        var fresh = factory.CountRequests("lb.test", "/1/explore/fresh-releases");
        await factory.RunReleasesJobAsync(admin);
        Assert.Equal(browsed, factory.CountRequests("mb.test", "/ws/2/release-group"));
        Assert.Equal(lookedUp, factory.CountRequests("mb.test", "/ws/2/release/"));
        Assert.Equal(fresh, factory.CountRequests("lb.test", "/1/explore/fresh-releases"));
    }

    [Fact]
    public async Task Contributors_see_recent_and_upcoming_releases_of_the_library_artists()
    {
        var admin = await factory.ReadyAsync();
        var contributor = await AccountAsync(admin, "rel-contributor", Roles.Contributor);

        var all = await ReleasesAsync(contributor);
        Assert.Equal(["Fresh EP", "Live", "New Album", "Next Single"], all.Select(r => r.Title).Order());
        var live = all.Single(r => r.Title == "Live");
        Assert.NotNull(live.AlbumId);
        Assert.Equal(await ArtistIdAsync("Other Band"), live.ArtistId);
        var single = all.Single(r => r.Title == "Next Single");
        Assert.True(single.Upcoming);
        Assert.Equal("single", single.Type);
        Assert.Null(single.AlbumId);
        var newAlbum = all.Single(r => r.Title == "New Album");
        Assert.Equal(("Test Artist", ReleasesFactory.NewAlbumGroup, false), (newAlbum.ArtistName, newAlbum.ReleaseGroupId, newAlbum.Upcoming));

        Assert.Equal(["Next Single"], (await ReleasesAsync(contributor, "?when=upcoming")).Select(r => r.Title));
        Assert.Equal(["Fresh EP"], (await ReleasesAsync(contributor, "?type=ep")).Select(r => r.Title));
        Assert.Equal(["Live", "New Album"], (await ReleasesAsync(contributor, "?when=recent&type=album")).Select(r => r.Title).Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await contributor.GetAsync("/api/releases?when=hier", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/releases", Ct)).StatusCode);
    }

    [Fact]
    public async Task Simple_users_only_see_releases_already_in_the_library()
    {
        var admin = await factory.ReadyAsync();
        var user = await AccountAsync(admin, "rel-user", Roles.User);

        Assert.Equal(["Live"], (await ReleasesAsync(user)).Select(r => r.Title));
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/artists/{await ArtistIdAsync("Test Artist")}/releases", Ct)).StatusCode);
        var home = await user.GetFromJsonAsync<HomeDto>("/api/home", Ct);
        Assert.Equal(["Live"], home!.NewReleases!.Select(r => r.Title));
    }

    [Fact]
    public async Task The_artist_page_lists_the_releases_missing_from_the_library()
    {
        var admin = await factory.ReadyAsync();
        var contributor = await AccountAsync(admin, "rel-artist-page", Roles.Contributor);

        var missing = (await contributor.GetFromJsonAsync<List<ArtistReleaseDto>>($"/api/artists/{await ArtistIdAsync("Test Artist")}/releases", Ct))!;

        Assert.Equal(["Next Single", "Fresh EP", "New Album", "Ancient EP"], missing.Select(r => r.Title));
        Assert.Empty((await contributor.GetFromJsonAsync<List<ArtistReleaseDto>>($"/api/artists/{await ArtistIdAsync("Other Band")}/releases", Ct))!);
    }

    [Fact]
    public async Task The_home_page_shows_new_releases_not_yet_in_the_library()
    {
        var admin = await factory.ReadyAsync();

        var home = await admin.GetFromJsonAsync<HomeDto>("/api/home", Ct);

        Assert.Equal(["Fresh EP", "New Album", "Next Single"], home!.NewReleases!.Select(r => r.Title).Order());
        Assert.Equal("Next Single", home.NewReleases![^1].Title);
    }

    [Fact]
    public async Task Release_covers_are_fetched_once_and_served_by_the_server()
    {
        await factory.ReadyAsync();
        var anonymous = factory.CreateClient();
        var tokens = await SonNuageFactory.LoginAsync(anonymous, "admin", SonNuageFactory.AdminPassword);

        var first = await anonymous.GetAsync($"/api/releases/{ReleasesFactory.NewAlbumGroup}/cover?t={tokens.MediaToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], await first.Content.ReadAsByteArrayAsync(Ct));
        var second = await anonymous.GetAsync($"/api/releases/{ReleasesFactory.NewAlbumGroup}/cover?t={tokens.MediaToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, factory.CountRequests("caa.test", $"/release-group/{ReleasesFactory.NewAlbumGroup}"));

        // Sans pochette : 404, retenu aussi. Groupe inconnu de la bibliothèque : jamais demandé.
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/releases/{ReleasesFactory.NextSingleGroup}/cover?t={tokens.MediaToken}", Ct)).StatusCode);
        Assert.Equal(1, factory.CountRequests("caa.test", $"/release-group/{ReleasesFactory.NextSingleGroup}"));
        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/releases/{unknown}/cover?t={tokens.MediaToken}", Ct)).StatusCode);
        Assert.Equal(0, factory.CountRequests("caa.test", $"/release-group/{unknown}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/releases/{ReleasesFactory.NewAlbumGroup}/cover", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_release_has_a_thirty_second_preview_for_contributors()
    {
        var admin = await factory.ReadyAsync();
        var client = factory.CreateClient();
        var tokens = await SonNuageFactory.LoginAsync(client, "admin", SonNuageFactory.AdminPassword);
        string Url(Guid group) => $"/api/releases/{group}/preview";

        // La vidéo de cette parution est connue et son extrait déjà sur le disque : il est servi tel quel.
        factory.Services.GetRequiredService<ReleasePreviews>().Remember(ReleasesFactory.NewAlbumGroup, "relprev0001", null);
        var dir = Path.Combine(factory.DataPath, "discovery", "previews");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "relprev0001.mp3"), new byte[1024], Ct);
        var ready = await client.GetAsync($"{Url(ReleasesFactory.NewAlbumGroup)}?t={tokens.MediaToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("audio/mpeg", ready.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1024, (await ready.Content.ReadAsByteArrayAsync(Ct)).Length);

        // Sans yt-dlp (tests), une autre parution ne peut pas être cherchée sur YouTube : erreur dite, pas de plantage.
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync($"{Url(ReleasesFactory.NextSingleGroup)}?t={tokens.MediaToken}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url(Guid.NewGuid())}?t={tokens.MediaToken}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url(ReleasesFactory.NewAlbumGroup), Ct)).StatusCode);
        await AccountAsync(admin, "release-preview-user", Roles.User);
        var user = await SonNuageFactory.LoginAsync(factory.CreateClient(), "release-preview-user", "password-1234");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url(ReleasesFactory.NewAlbumGroup)}?t={user.MediaToken}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_release_lists_its_songs_for_contributors()
    {
        var admin = await factory.ReadyAsync();
        var contributor = await AccountAsync(admin, "release-songs-contrib", Roles.Contributor);
        var url = $"/api/releases/{ReleasesFactory.NewAlbumGroup}/tracks";

        var songs = await contributor.GetFromJsonAsync<List<ReleaseTrackDto>>(url, Ct);
        Assert.Equal([new ReleaseTrackDto("Opening Song", 200), new ReleaseTrackDto("Second Song", 181)], songs);
        // Lue une fois chez Deezer, puis retenue.
        await contributor.GetFromJsonAsync<List<ReleaseTrackDto>>(url, Ct);
        Assert.Equal(1, factory.CountRequests("dz.test", "/album/77/tracks"));

        // Deezer ne connaît pas cette parution : liste vide, pas une erreur.
        Assert.Empty((await contributor.GetFromJsonAsync<List<ReleaseTrackDto>>($"/api/releases/{ReleasesFactory.FreshEpGroup}/tracks", Ct))!);

        Assert.Equal(HttpStatusCode.NotFound, (await contributor.GetAsync($"/api/releases/{Guid.NewGuid()}/tracks", Ct)).StatusCode);
        var user = await AccountAsync(admin, "release-songs-user", Roles.User);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_starts_a_refresh()
    {
        var admin = await factory.ReadyAsync();
        var contributor = await AccountAsync(admin, "rel-refresh", Roles.Contributor);

        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.PostAsync("/api/admin/releases/refresh", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/releases/refresh", null, Ct)).StatusCode);
    }
}
