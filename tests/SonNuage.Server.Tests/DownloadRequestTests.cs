using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Tests;

/// <summary>Demandes de téléchargement : rôle Contributeur, quota par jour, approbation par un admin ou automatique.</summary>
public sealed class DownloadRequestTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, UserDto User)> AccountAsync(string name, string role, int? perDay = null, bool? auto = null)
    {
        var admin = await factory.AdminClientAsync();
        var user = (await (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", role), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<UserDto>(Ct))!;
        if (perDay is not null || auto is not null)
            user = (await (await admin.PatchAsJsonAsync($"/api/admin/users/{user.Id}", new UpdateUserRequest(null, null, perDay, auto), Ct))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<UserDto>(Ct))!;
        return (await factory.ClientAsync(name, $"{name}-password"), user);
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, string video, string title = "Un morceau") =>
        client.PostAsJsonAsync("/api/requests", new CreateDownloadRequest(video, title, "Une chaîne", 200, null), Ct);

    private static async Task<DownloadRequestDto> AskOkAsync(HttpClient client, string video, string title = "Un morceau") =>
        (await (await AskAsync(client, video, title)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DownloadRequestDto>(Ct))!;

    [Fact]
    public async Task Regular_users_cannot_request_and_contributors_cannot_administer()
    {
        var (user, _) = await AccountAsync("req-regular", Roles.User);
        var (contributor, _) = await AccountAsync("req-no-admin", Roles.Contributor);

        Assert.Equal(HttpStatusCode.Forbidden, (await AskAsync(user, "aaaaaaaaaa1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/requests/mine", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.GetAsync("/api/admin/requests", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.GetAsync("/api/admin/youtube/items", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_admin_sets_a_contributor_quota_and_auto_approval()
    {
        var (_, created) = await AccountAsync("req-settings", Roles.Contributor);
        Assert.Equal(Roles.Contributor, created.Role);
        Assert.Equal(100, created.RequestsPerDay);
        Assert.False(created.AutoApproveRequests);

        var admin = await factory.AdminClientAsync();
        var updated = await (await admin.PatchAsJsonAsync($"/api/admin/users/{created.Id}", new UpdateUserRequest(null, null, 5, true), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<UserDto>(Ct);
        Assert.Equal(5, updated!.RequestsPerDay);
        Assert.True(updated.AutoApproveRequests);

        var invalid = await admin.PatchAsJsonAsync($"/api/admin/users/{created.Id}", new UpdateUserRequest(null, null, 0, null), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task A_request_waits_for_an_admin_who_approves_or_denies_it()
    {
        var (contributor, user) = await AccountAsync("req-waits", Roles.Contributor);
        var first = await AskOkAsync(contributor, "https://www.youtube.com/watch?v=bbbbbbbbbb1&list=xyz", "Premier");
        var second = await AskOkAsync(contributor, "https://youtu.be/bbbbbbbbbb2", "Second");
        Assert.Equal("bbbbbbbbbb1", first.VideoId);
        Assert.Equal(DownloadRequestStatus.Requested, first.Status);
        Assert.Equal(user.Id, first.UserId);
        Assert.False(first.AutoApproved);
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().YoutubeItems.AnyAsync(i => i.VideoId == "bbbbbbbbbb1", Ct));

        var admin = await factory.AdminClientAsync();
        var approved = await (await admin.PostAsync($"/api/admin/requests/{first.Id}/approve", null, Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DownloadRequestDto>(Ct);
        var denied = await (await admin.PostAsJsonAsync($"/api/admin/requests/{second.Id}/deny", new DenyDownloadRequest("Déjà en CD"), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DownloadRequestDto>(Ct);

        Assert.NotEqual(DownloadRequestStatus.Requested, approved!.Status);
        Assert.Equal("admin", approved.DecidedBy);
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().YoutubeItems.AnyAsync(i => i.VideoId == "bbbbbbbbbb1", Ct));
        Assert.Equal(DownloadRequestStatus.Denied, denied!.Status);

        var mine = await contributor.GetFromJsonAsync<PagedResult<DownloadRequestDto>>("/api/requests/mine", Ct);
        Assert.Equal(["Second", "Premier"], mine!.Items.Select(r => r.Title));
        Assert.Equal("Déjà en CD", mine.Items[0].DenyReason);
        // Une demande refusée ne se refuse pas deux fois.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/requests/{second.Id}/deny", new DenyDownloadRequest(null), Ct)).StatusCode);
    }

    [Fact]
    public async Task Auto_approved_contributors_go_straight_to_the_download_queue()
    {
        var (contributor, _) = await AccountAsync("req-auto", Roles.Contributor, auto: true);

        var request = await AskOkAsync(contributor, "cccccccccc1");

        Assert.True(request.AutoApproved);
        Assert.NotEqual(DownloadRequestStatus.Requested, request.Status);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().YoutubeItems.AnyAsync(i => i.VideoId == "cccccccccc1", Ct));
    }

    [Fact]
    public async Task The_daily_quota_counts_the_last_24_hours()
    {
        var (contributor, user) = await AccountAsync("req-quota", Roles.Contributor, perDay: 2);
        var old = await AskOkAsync(contributor, "dddddddddd1");
        await AskOkAsync(contributor, "dddddddddd2");

        var over = await AskAsync(contributor, "dddddddddd3");
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal(new RequestQuotaDto(2, 2, false), await contributor.GetFromJsonAsync<RequestQuotaDto>("/api/requests/quota", Ct));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var entity = await db.DownloadRequests.SingleAsync(r => r.Id == old.Id, Ct);
            entity.CreatedAt = DateTimeOffset.UtcNow.AddHours(-25);
            await db.SaveChangesAsync(Ct);
        }
        (await AskAsync(contributor, "dddddddddd3")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Duplicates_and_non_youtube_addresses_are_refused()
    {
        var (contributor, _) = await AccountAsync("req-dupes", Roles.Contributor);
        await AskOkAsync(contributor, "eeeeeeeeee1");

        Assert.Equal(HttpStatusCode.Conflict, (await AskAsync(contributor, "https://music.youtube.com/watch?v=eeeeeeeeee1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AskAsync(contributor, "https://example.com/watch?v=eeeeeeeeee2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AskAsync(contributor, "pas une vidéo")).StatusCode);
    }

    [Fact]
    public async Task Admins_list_requests_newest_first_with_status_and_text_filters()
    {
        var (contributor, _) = await AccountAsync("req-list", Roles.Contributor);
        var kept = await AskOkAsync(contributor, "ffffffffff1", "Chanson à filtrer");
        var other = await AskOkAsync(contributor, "ffffffffff2", "Autre chanson");
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/requests/{other.Id}/deny", new DenyDownloadRequest(null), Ct)).EnsureSuccessStatusCode();

        var latest = await admin.GetFromJsonAsync<PagedResult<DownloadRequestDto>>("/api/admin/requests?limit=50", Ct);
        Assert.Equal(other.Id, latest!.Items[0].Id);

        var requested = await admin.GetFromJsonAsync<PagedResult<DownloadRequestDto>>("/api/admin/requests?status=requested&q=req-list", Ct);
        Assert.Equal([kept.Id], requested!.Items.Select(r => r.Id));

        var byTitle = await admin.GetFromJsonAsync<PagedResult<DownloadRequestDto>>("/api/admin/requests?q=filtrer", Ct);
        Assert.Equal([kept.Id], byTitle!.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Matching_by_artist_and_title_is_for_contributors_and_needs_both()
    {
        var (user, _) = await AccountAsync("match-regular", Roles.User);
        var (contributor, _) = await AccountAsync("match-contrib", Roles.Contributor);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/requests/match?artist=Daft%20Punk&title=Get%20Lucky", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await contributor.GetAsync("/api/requests/match?artist=Daft%20Punk&title=", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await contributor.GetAsync("/api/requests/match?artist=&title=Get%20Lucky", Ct)).StatusCode);
        // Sans yt-dlp (tests) : la recherche échoue proprement, sans appeler MusicBrainz pour rien.
        Assert.Equal(HttpStatusCode.BadGateway, (await contributor.GetAsync("/api/requests/match?artist=Daft%20Punk&title=Get%20Lucky&duration=248", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_whole_album_is_requested_in_the_background_by_contributors()
    {
        var (user, _) = await AccountAsync("album-regular", Roles.User);
        var (contributor, me) = await AccountAsync("album-contrib", Roles.Contributor);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/requests/album", new CreateAlbumRequest("Daft Punk", "Discovery"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await contributor.PostAsJsonAsync("/api/requests/album", new CreateAlbumRequest("Daft Punk", " "), Ct)).StatusCode);

        var response = await contributor.PostAsJsonAsync("/api/requests/album", new CreateAlbumRequest("Daft Punk", "Discovery"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        Assert.True(await db.JobSuites.AnyAsync(s => s.Kind == "requests.album" && s.CreatedById == me.Id, Ct));
    }
}
