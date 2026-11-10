using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Radio;

namespace SonNuage.Server.Tests;

/// <summary>
/// Recherche par ambiance de bout en bout. Bibliothèque de test : « Opening » calme, « Second Song » rock énergique de
/// 1994, « Encore » sans analyse mais tagué « chillout ».
/// </summary>
public sealed class AmbianceSearchTests(RadioFactory factory) : IClassFixture<RadioFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SessionAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        if (!await db.AudioFeatures.AnyAsync(Ct))
        {
            var opening = await db.Tracks.Where(t => t.Title == "Opening").Select(t => t.Id).SingleAsync(Ct);
            var encore = await db.Tracks.Where(t => t.Title == "Encore").Select(t => t.Id).SingleAsync(Ct);
            await db.Tracks.Where(t => t.Title == "Second Song").ExecuteUpdateAsync(s => s.SetProperty(t => t.Genre, "Rock").SetProperty(t => t.Year, 1994), Ct);
            db.AudioFeatures.Add(Features(opening, relaxed: 0.95, energy: 0.36, instrumental: 0.9, bpm: 80));
            foreach (var loud in await db.Tracks.Where(t => t.Title == "Second Song").Select(t => t.Id).ToListAsync(Ct))
                db.AudioFeatures.Add(Features(loud, relaxed: 0.05, energy: 0.74, instrumental: 0.1, bpm: 150));
            db.TrackTags.Add(new TrackTag { TrackId = encore, Name = "chillout", Count = 2 });
            await db.SaveChangesAsync(Ct);
        }
        factory.Services.GetRequiredService<RadioStations>().Forget();
        return admin;
    }

    private static TrackAudioFeatures Features(Guid id, double relaxed, double energy, double instrumental, double bpm) => new()
    {
        TrackId = id, Version = TrackAudioFeatures.CurrentVersion, AnalyzedAt = DateTimeOffset.UtcNow, Relaxed = relaxed, Energy = energy,
        Instrumental = instrumental, Bpm = bpm, Acoustic = relaxed, Aggressive = 1 - relaxed, Danceability = 1 - relaxed,
        Party = 1 - relaxed, Happy = 0.5, Sad = 0.3, Valence = 0.55,
    };

    private static async Task<AmbianceResultDto?> AmbianceAsync(HttpClient client, string q)
    {
        var response = await client.GetAsync($"/api/search/ambiance?q={Uri.EscapeDataString(q)}", Ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<AmbianceResultDto>(Ct);
    }

    [Fact]
    public async Task A_calm_request_finds_calm_tracks_analysed_or_tagged()
    {
        var client = await SessionAsync();

        var result = await AmbianceAsync(client, "Musique calme pour travailler");

        Assert.NotNull(result);
        Assert.Equal("ambiance:musique calme pour travailler", result.StationId);
        Assert.Equal(["calme", "travailler"], result.Terms);
        Assert.Equal("Opening", Assert.Single(result.Tracks).Title);
        // Sans analyse, le tag suffit pour « calme » (mais pas pour « travailler »).
        var calm = (await AmbianceAsync(client, "calme"))!.Tracks.Select(t => t.Title).ToList();
        Assert.Equal(["Opening", "Encore"], calm);
    }

    [Fact]
    public async Task Genre_mood_and_decade_narrow_the_choice()
    {
        var client = await SessionAsync();

        var result = await AmbianceAsync(client, "rock énergique années 90");

        Assert.NotNull(result);
        Assert.NotEmpty(result.Tracks);
        Assert.All(result.Tracks, t => Assert.Equal("Second Song", t.Title));
    }

    [Fact]
    public async Task Names_from_the_library_are_not_ambiances()
    {
        var client = await SessionAsync();
        Assert.Null(await AmbianceAsync(client, "Second Song"));
        Assert.Null(await AmbianceAsync(client, "  "));

        // Un titre « Triste » et un album « Soirée » : leur nom tapé tel quel, c'est eux qu'on cherche.
        await RenameAsync("Encore", "Triste", "triste otherband live otherband", "Live", "Soirée", "soiree");
        try
        {
            Assert.Null(await AmbianceAsync(client, "triste"));
            Assert.Null(await AmbianceAsync(client, "Soirée"));
            Assert.NotNull(await AmbianceAsync(client, "chanson triste"));
        }
        finally
        {
            await RenameAsync("Triste", "Encore", "encore otherband live otherband", "Soirée", "Live", "live");
        }
    }

    private async Task RenameAsync(string track, string title, string searchKey, string album, string albumTitle, string normalized)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        await db.Tracks.Where(t => t.Title == track).ExecuteUpdateAsync(s => s.SetProperty(t => t.Title, title).SetProperty(t => t.SearchKey, searchKey), Ct);
        await db.Albums.Where(a => a.Title == album).ExecuteUpdateAsync(s => s.SetProperty(a => a.Title, albumTitle)
            .SetProperty(a => a.NormalizedTitle, normalized), Ct);
    }

    [Fact]
    public async Task An_ambiance_plays_as_an_endless_radio()
    {
        var client = await SessionAsync();
        var station = (await AmbianceAsync(client, "calme"))!.StationId;

        var first = await NextAsync(client, station, []);
        Assert.NotEmpty(first);
        Assert.DoesNotContain(first, t => t.Title == "Second Song");

        var next = await NextAsync(client, station, [first[0].Id]);
        Assert.DoesNotContain(next, t => t.Id == first[0].Id);

        var unknown = await client.PostAsJsonAsync($"/api/radio/stations/{Uri.EscapeDataString("ambiance:metallica")}/next",
            new StationNextRequest([], 5), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<List<TrackDto>> NextAsync(HttpClient client, string station, IReadOnlyList<Guid> exclude)
    {
        var response = await client.PostAsJsonAsync($"/api/radio/stations/{Uri.EscapeDataString(station)}/next",
            new StationNextRequest(exclude, 10), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<TrackDto>>(Ct))!;
    }
}
