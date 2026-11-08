using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.Server.Tests;

public sealed class ArtistCreditTests
{
    [Theory]
    // Séparateurs sûrs : toujours coupés, le premier nom est l'artiste principal.
    [InlineData("OrelSan feat. Damso", "Various Artists", "Song", "OrelSan", "Damso")]
    [InlineData("OrelSan Feat Damso", "OrelSan", "Song", "OrelSan", "Damso")]
    [InlineData("OrelSan ft. Damso & Gringe", "OrelSan", "Song", "OrelSan", "Damso|Gringe")]
    [InlineData("OrelSan featuring Damso, Nekfeu", "OrelSan", "Song", "OrelSan", "Damso|Nekfeu")]
    [InlineData("OrelSan (feat. Damso)", "OrelSan", "Song", "OrelSan", "Damso")]
    [InlineData("Daft Punk [ft. Pharrell Williams]", "Daft Punk", "Song", "Daft Punk", "Pharrell Williams")]
    // Séparateurs ambigus : coupés seulement si le premier nom est l'artiste de l'album.
    [InlineData("OrelSan & Damso", "OrelSan", "Song", "OrelSan", "Damso")]
    [InlineData("OrelSan x Damso", "OrelSan", "Song", "OrelSan", "Damso")]
    [InlineData("OrelSan avec Damso", "OrelSan", "Song", "OrelSan", "Damso")]
    [InlineData("OrelSan, Damso", "Orelsan", "Song", "OrelSan", "Damso")]
    [InlineData("OrelSan & Gringe", "Various Artists", "Song", "OrelSan & Gringe", "")]
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel", "Song", "Simon & Garfunkel", "")]
    [InlineData("Earth, Wind & Fire", "Earth, Wind & Fire", "Song", "Earth, Wind & Fire", "")]
    [InlineData("Malcolm X", "Malcolm X", "Song", "Malcolm X", "")]
    // Invités cités dans le titre, entre parenthèses ou crochets seulement.
    [InlineData("Damso", "Damso", "Z (feat. OrelSan)", "Damso", "OrelSan")]
    [InlineData("Damso", "Damso", "Feat Lounge", "Damso", "")]
    // Rien de reconnaissable : le crédit reste entier.
    [InlineData("feat. Damso", "Damso", "Song", "feat. Damso", "")]
    [InlineData("Damso feat. Damso", "Damso", "Song", "Damso", "")]
    public void Splits_a_credit_into_main_artist_and_guests(string credit, string albumArtist, string title, string main, string guests)
    {
        var parsed = ArtistCredit.Parse(credit, albumArtist, title);
        Assert.Equal(main, parsed.Main);
        Assert.Equal(guests.Length == 0 ? [] : guests.Split('|'), parsed.Guests);
    }
}

/// <summary>Bibliothèque avec des featurings, tagués comme les écrirait Picard.</summary>
public sealed class FeaturingLibraryFactory : SonNuageFactory
{
    public FeaturingLibraryFactory()
    {
        Tagged("OrelSan", "Civilisation", "01.wav", "Solo", "OrelSan", "OrelSan");
        Tagged("OrelSan", "Civilisation", "02.wav", "Duo", "OrelSan feat. Damso", "OrelSan");
        Tagged("Damso", "Ipseite", "01.wav", "Seul", "Damso", "Damso");
        Tagged("Damso", "Ipseite", "03.wav", "Trio", "Damso feat. Nekfeu", "Damso");
        Tagged("Damso", "Ipseite", "02.wav", "Retour (feat. OrelSan)", "Damso", "Damso");
        Tagged("Various", "Compil", "01.wav", "Ensemble", "OrelSan & Gringe", "Various Artists");
        Tagged("Various", "Compil", "02.wav", "Collab", "OrelSan ft. Nekfeu", "Various Artists");
    }

    private void Tagged(string folder, string album, string file, string title, string artist, string albumArtist)
    {
        var path = Path.Combine(MusicPath, folder, album, file);
        WriteWav(path, seconds: 1);
        TaggedLibraryFactory.TagWav(path, tag =>
        {
            tag.Title = title;
            tag.Performers = [artist];
            tag.AlbumArtists = [albumArtist];
            tag.Album = album;
        });
    }
}

public sealed class FeaturingTests(FeaturingLibraryFactory factory) : IClassFixture<FeaturingLibraryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> ArtistIdAsync(HttpClient admin, string name)
    {
        var page = await admin.GetFromJsonAsync<PagedResult<ArtistDto>>("/api/artists?limit=200", Ct);
        return Assert.Single(page!.Items, a => a.Name == name).Id;
    }

    private static async Task<List<string>> TitlesAsync(HttpClient admin, string url) =>
        (await admin.GetFromJsonAsync<List<TrackDto>>(url, Ct))!.Select(t => t.Title).Order().ToList();

    private async Task AssertCreditsAsync(HttpClient admin)
    {
        var orelsan = await ArtistIdAsync(admin, "OrelSan");
        var damso = await ArtistIdAsync(admin, "Damso");

        // « Collab » est sur une compilation : il rejoint OrelSan par son crédit.
        Assert.Equal(["Collab", "Duo", "Solo"], await TitlesAsync(admin, $"/api/artists/{orelsan}/tracks"));
        Assert.Equal(["Retour (feat. OrelSan)"], await TitlesAsync(admin, $"/api/artists/{orelsan}/appearances"));
        Assert.Equal(["Duo"], await TitlesAsync(admin, $"/api/artists/{damso}/appearances"));
        var detail = await admin.GetFromJsonAsync<ArtistDetailDto>($"/api/artists/{orelsan}", Ct);
        Assert.Equal(3, detail!.Artist.TrackCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        Assert.False(await db.Artists.AnyAsync(a => a.Name == "OrelSan feat. Damso" || a.Name == "OrelSan ft. Nekfeu", Ct));
        // Prudence : « & » sur la compilation d'un autre artiste reste un crédit entier.
        Assert.True(await db.Artists.AnyAsync(a => a.Name == "OrelSan & Gringe", Ct));
    }

    [Fact]
    public async Task A_featuring_belongs_to_the_main_artist_and_each_guest_lists_where_they_appear()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        await AssertCreditsAsync(admin);
    }

    [Fact]
    public async Task A_full_scan_relinks_tracks_indexed_before_featurings_were_understood()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        using (var scope = factory.Services.CreateScope())
        {
            // État d'avant : le crédit entier est un artiste à part, sans invité.
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var key = NameNormalizer.Key("OrelSan feat. Damso");
            var legacy = await db.Artists.FirstOrDefaultAsync(a => a.NormalizedName == key, Ct);
            if (legacy is null)
            {
                legacy = new Artist { Name = "OrelSan feat. Damso", NormalizedName = key };
                db.Artists.Add(legacy);
                await db.SaveChangesAsync(Ct);
            }
            await db.TrackGuests.ExecuteDeleteAsync(Ct);
            await db.Tracks.Where(t => t.Title == "Duo").ExecuteUpdateAsync(s => s.SetProperty(t => t.ArtistId, legacy.Id), Ct);
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LibraryScanner>().ScanAllAsync(null, Ct);

        await AssertCreditsAsync(admin);
    }

    [Fact]
    public async Task Correcting_a_credit_by_hand_removes_the_guest()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        Guid trio, nekfeu;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            trio = await db.Tracks.Where(t => t.Title == "Trio").Select(t => t.Id).SingleAsync(Ct);
            nekfeu = await db.Artists.Where(a => a.Name == "Nekfeu").Select(a => a.Id).SingleAsync(Ct);
        }
        Assert.Contains("Trio", await TitlesAsync(admin, $"/api/artists/{nekfeu}/appearances"));

        (await admin.PutAsJsonAsync($"/api/admin/tracks/{trio}/metadata",
            new UpdateTrackMetadataRequest("Trio", "Damso", "Ipseite", "Damso", 3, null, null, null), Ct)).EnsureSuccessStatusCode();

        Assert.DoesNotContain("Trio", await TitlesAsync(admin, $"/api/artists/{nekfeu}/appearances"));
        Assert.Contains("Collab", await TitlesAsync(admin, $"/api/artists/{nekfeu}/appearances"));
    }
}
