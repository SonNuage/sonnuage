using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Tests;

/// <summary>
/// Garde-fous disque réglables par l'admin : espace libre minimum (en dessous, plus aucun téléchargement) et place
/// allouée aux extraits. Un minimum énorme (100 To) simule un disque plein.
/// </summary>
public sealed class StorageGuardTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly StorageSettingsDto Full = new(MinFreeGb: 100_000, PreviewQuotaGb: 2);

    [Fact]
    public async Task An_admin_reads_and_changes_the_limits()
    {
        var admin = await factory.AdminClientAsync();
        var status = await admin.GetFromJsonAsync<StorageStatusDto>("/api/admin/storage", Ct);
        Assert.Equal(StorageSettingsDto.Defaults, status!.Settings);
        Assert.NotNull(status.FreeBytes);
        Assert.False(status.DownloadsBlocked);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/storage", new StorageSettingsDto(-1, 2), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/storage", new StorageSettingsDto(5, 0), Ct)).StatusCode);

        var saved = await (await admin.PutAsJsonAsync("/api/admin/storage", new StorageSettingsDto(10, 0.5), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<StorageStatusDto>(Ct);
        Assert.Equal(new StorageSettingsDto(10, 0.5), saved!.Settings);
        (await admin.PutAsJsonAsync("/api/admin/storage", StorageSettingsDto.Defaults, Ct)).EnsureSuccessStatusCode();

        var contributorClient = await ContributorAsync("storage-contrib");
        Assert.Equal(HttpStatusCode.Forbidden, (await contributorClient.GetAsync("/api/admin/storage", Ct)).StatusCode);
    }

    [Fact]
    public async Task Below_the_free_space_minimum_downloads_and_previews_wait()
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PutAsJsonAsync("/api/admin/storage", Full, Ct)).EnsureSuccessStatusCode();
        try
        {
            var status = await admin.GetFromJsonAsync<StorageStatusDto>("/api/admin/storage", Ct);
            Assert.True(status!.DownloadsBlocked);

            var preview = await admin.GetAsync("/api/requests/preview/dddddddddd1", Ct);
            Assert.Equal(HttpStatusCode.InsufficientStorage, preview.StatusCode);

            // Demande d'un admin : acceptée d'office, mise en file, mais le téléchargement attend la place.
            var request = await (await admin.PostAsJsonAsync("/api/requests", new CreateDownloadRequest("dddddddddd2", "Plein", null, 200, null), Ct))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DownloadRequestDto>(Ct);
            Job? job = null;
            for (var i = 0; i < 100 && job?.Error is null; i++)
            {
                await Task.Delay(100, Ct);
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var item = await db.YoutubeItems.AsNoTracking().FirstAsync(y => y.VideoId == "dddddddddd2", Ct);
                job = await db.Jobs.AsNoTracking().Where(j => j.SubjectId == item.Id).OrderBy(j => j.NotBefore).LastOrDefaultAsync(Ct);
                Assert.Equal(YoutubeItemStatus.Queued, item.Status);
            }
            Assert.NotNull(job?.Error);
            Assert.Equal(JobStatus.Queued, job!.Status);
            Assert.True(job.NotBefore > DateTimeOffset.UtcNow);
            Assert.Equal(DownloadRequestStatus.Accepted, request!.Status);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/admin/storage", StorageSettingsDto.Defaults, Ct)).EnsureSuccessStatusCode();
        }
    }

    private async Task<HttpClient> ContributorAsync(string name)
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", Roles.Contributor), Ct)).EnsureSuccessStatusCode();
        return await factory.ClientAsync(name, $"{name}-password");
    }
}
