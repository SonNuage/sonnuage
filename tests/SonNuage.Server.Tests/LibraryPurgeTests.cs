using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class LibraryPurgeTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Artists_without_albums_are_hidden_and_empty_albums_are_purged()
    {
        var admin = await factory.AdminClientAsync();
        var scans = factory.Services.GetRequiredService<ScanCoordinator>();
        await scans.ScanFilesNowAsync([], Ct);

        Guid creditId, emptyAlbumId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            // Crédit d'un titre (« X feat. Y ») : l'artiste existe, mais sans album à lui.
            var track = await db.Tracks.FirstAsync(Ct);
            var credit = new Artist { Name = "Test Artist feat. Guest", NormalizedName = "test artist feat guest" };
            db.Artists.Add(credit);
            track.ArtistId = credit.Id;
            var owner = await db.Artists.FirstAsync(a => a.Albums.Any(), Ct);
            var empty = new Album { Title = "Vide", NormalizedTitle = "vide", ArtistId = owner.Id };
            db.Albums.Add(empty);
            await db.SaveChangesAsync(Ct);
            (creditId, emptyAlbumId) = (credit.Id, empty.Id);
        }

        var artists = await admin.GetFromJsonAsync<PagedResult<ArtistDto>>("/api/artists?limit=200", Ct);
        Assert.DoesNotContain(artists!.Items, a => a.Id == creditId);
        var search = await admin.GetFromJsonAsync<SearchResultDto>("/api/search?q=guest", Ct);
        Assert.DoesNotContain(search!.Artists, a => a.Id == creditId);

        Assert.True(await scans.PurgeEmptyAsync(Ct) >= 1);
        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        Assert.False(await after.Albums.AnyAsync(a => a.Id == emptyAlbumId, Ct));
        // L'artiste crédité reste : un titre pointe encore vers lui.
        Assert.True(await after.Artists.AnyAsync(a => a.Id == creditId, Ct));
    }
}
