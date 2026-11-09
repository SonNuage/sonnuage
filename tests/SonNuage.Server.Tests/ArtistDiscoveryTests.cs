using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;

namespace SonNuage.Server.Tests;

/// <summary>
/// Découverte par artiste : artistes proches de ceux de la bibliothèque (ListenBrainz), leurs titres phares, retrouvés
/// sur YouTube avec un extrait pré-téléchargé. Réservée aux contributeurs et aux admins.
/// </summary>
public sealed class ArtistDiscoveryTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ListenBrainz_similar_artists_and_top_recordings_are_read()
    {
        var similar = ListenBrainzClient.ParseSimilar("""
            [{"artist_mbid":"c6d571dd-c0ae-4ac8-9500-780b1b9b25e5","name":"Nile Rodgers","comment":"","type":"Person","score":10016,"reference_mbid":"x"},
             {"artist_mbid":"e21857d5-3256-4547-afb3-4b6ded592596","name":"Gorillaz","comment":"virtual band","score":8405},
             {"name":"Sans identifiant","score":10}]
            """);
        Assert.Equal([("Nile Rodgers", 10016.0, null), ("Gorillaz", 8405.0, "virtual band")], similar.Select(s => (s.Name, s.Score, s.Comment)));

        var top = ListenBrainzClient.ParseTopRecordings("""
            [{"artist_name":"Daft Punk","recording_mbid":"f1a6a40f-78f5-4918-968d-f64363bae94c","recording_name":"Harder, Better, Faster, Stronger","length":224293,"release_name":"Discovery","total_listen_count":5},
             {"recording_name":"Sans durée","recording_mbid":"r2"}]
            """);
        Assert.Equal("Harder, Better, Faster, Stronger", top[0].Title);
        Assert.Equal(224.293, top[0].LengthSeconds!.Value, 3);
        Assert.Equal("Discovery", top[0].ReleaseName);
        Assert.Null(top[1].LengthSeconds);
    }

    [Fact]
    public void Deezer_gives_the_top_songs_of_the_artist_with_the_same_name()
    {
        var id = DeezerClient.ParseArtistId("""
            {"data":[{"id":1,"name":"Pink Floyd Tribute","nb_fan":9},{"id":2,"name":"Pink Floyd","nb_fan":12},{"id":860,"name":"Pink  Floyd","nb_fan":6400000}],"total":3}
            """, "Pink Floyd");
        Assert.Equal(860, id);
        Assert.Null(DeezerClient.ParseArtistId("""{"data":[{"id":1,"name":"Pink Floyd Tribute"}]}""", "Pink Floyd"));
        Assert.Null(DeezerClient.ParseArtistId("""{"error":{"type":"Exception","code":4}}""", "Pink Floyd"));

        var top = DeezerClient.ParseTop("""
            {"data":[{"id":9,"title":"Guest Song","duration":200,"artist":{"id":77}},
                     {"id":116914042,"title":"Wish You Were Here (2011 Remaster)","title_short":"Wish You Were Here","duration":334,"album":{"title":"Wish You Were Here"},"artist":{"id":860}},
                     {"id":2,"title":"Time","duration":0,"artist":{"id":860}},
                     {"id":3,"duration":10}]}
            """, 860);
        // Les titres de l'artiste d'abord, puis ceux où il n'est qu'invité.
        Assert.Equal([("Wish You Were Here", 334.0, "Wish You Were Here"), ("Time", null, null), ("Guest Song", 200.0, null)],
            top.Select(t => (t.Title, t.LengthSeconds, t.ReleaseName)));
        Assert.All(top, t => Assert.Null(t.RecordingMbid));
    }

    [Fact]
    public void Deezer_finds_the_album_of_the_artist_and_its_tracks_in_order()
    {
        var search = """
            {"data":[{"id":1,"title":"Discovery (Tribute)","artist":{"name":"Daft Punk"}},
                     {"id":2,"title":"Discovery","artist":{"name":"Electric Light Orchestra"}},
                     {"id":302127,"title":"Discovery","artist":{"name":"Daft Punk"}},
                     {"id":3,"title":"Discovery (Deluxe Edition)","artist":{"name":"Daft Punk"}}]}
            """;
        Assert.Equal(302127, DeezerClient.ParseAlbumId(search, "Daft Punk", "Discovery"));
        // Sans titre exact, une édition de l'album (« Deluxe », « Remastered »…) fait l'affaire.
        Assert.Equal(3, DeezerClient.ParseAlbumId(search.Replace("\"id\":302127", "\"id\":4").Replace("\"title\":\"Discovery\",\"artist\":{\"name\":\"Daft Punk\"}", "\"title\":\"Autre\",\"artist\":{\"name\":\"Daft Punk\"}"), "Daft Punk", "Discovery"));
        Assert.Null(DeezerClient.ParseAlbumId(search, "Justice", "Discovery"));

        var tracks = DeezerClient.ParseAlbumTracks("""
            {"data":[{"id":1,"title":"One More Time","duration":320,"artist":{"name":"Daft Punk"}},
                     {"id":2,"title":"Aerodynamic","title_short":"Aerodynamic","duration":212},
                     {"id":3,"duration":10}]}
            """);
        Assert.Equal([("One More Time", 320.0), ("Aerodynamic", 212.0)], tracks.Select(t => (t.Title, t.LengthSeconds!.Value)));
    }

    [Fact]
    public void Each_refresh_adds_new_artists_and_drops_those_older_than_seven_days()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        RankedArtist R(string id, double score) => new(id, id, null, score, []);
        var ranked = new[] { R("a", 9), R("b", 8), R("c", 7), R("d", 6), R("e", 5) };

        // « a » a 8 jours : il part, et ne revient pas dans la même actualisation (mais pourra revenir ensuite).
        var plan = DiscoveryRanker.Plan(ranked, [("a", now.AddDays(-8)), ("b", now.AddDays(-1))], now, newPerRun: 2, minShown: 1);
        Assert.Equal(["a"], plan.Expired);
        Assert.Equal(["c", "d"], plan.Added.Select(r => r.MusicBrainzId));

        // Moins d'artistes que le minimum affiché : on complète jusqu'au minimum.
        var first = DiscoveryRanker.Plan(ranked, [], now, newPerRun: 2, minShown: 4);
        Assert.Equal(["a", "b", "c", "d"], first.Added.Select(r => r.MusicBrainzId));
        Assert.Empty(first.Expired);

        // Plus rien de neuf : rien n'est ajouté, rien de récent n'est retiré.
        var none = DiscoveryRanker.Plan([R("b", 8)], [("b", now.AddDays(-6.9))], now, newPerRun: 2, minShown: 1);
        Assert.Empty(none.Added);
        Assert.Empty(none.Expired);
    }

    [Fact]
    public void Similar_artists_are_ranked_across_seeds_without_those_already_in_the_library()
    {
        var ranked = DiscoveryRanker.Rank(
        [
            new DiscoverySeed("Daft Punk", 1.0, [new("m-nile", "Nile Rodgers", null, 100), new("m-gorillaz", "Gorillaz", "virtual band", 80), new("m-justice", "Justice", null, 60)]),
            new DiscoverySeed("Justice", 0.5, [new("m-gorillaz", "Gorillaz", "virtual band", 100), new("m-daft", "Daft Punk", null, 90)]),
        ], libraryMbids: ["m-daft"], libraryNames: ["Justice", "Daft Punk"], max: 10);

        Assert.Equal(["Gorillaz", "Nile Rodgers"], ranked.Select(r => r.Name));
        Assert.Equal(["Daft Punk", "Justice"], ranked[0].BecauseOf);
        Assert.Equal("virtual band", ranked[0].Comment);
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public async Task Contributors_see_discovered_artists_and_admins_refresh_them()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var artist = new DiscoveryArtist { MusicBrainzId = "m-gorillaz", Name = "Gorillaz", Comment = "virtual band", Score = 1.5, BecauseOf = "Daft Punk\nJustice", UpdatedAt = DateTimeOffset.UtcNow };
            artist.Tracks.Add(new DiscoveryTrack { Rank = 0, Title = "Feel Good Inc.", DurationSeconds = 222, VideoId = "HyHNuVaZJ-k", VideoTitle = "Gorillaz - Feel Good Inc.", Channel = "Gorillaz", MatchScore = 95 });
            artist.Tracks.Add(new DiscoveryTrack { Rank = 1, Title = "Pas trouvé" });
            db.DiscoveryArtists.Add(artist);
            await db.SaveChangesAsync(Ct);
        }

        var admin = await factory.AdminClientAsync();
        var page = await admin.GetFromJsonAsync<DiscoveryPageDto>("/api/discovery/artists", Ct);
        var gorillaz = Assert.Single(page!.Artists, a => a.Name == "Gorillaz");
        Assert.Equal(["Daft Punk", "Justice"], gorillaz.BecauseOf);
        Assert.Equal(["Feel Good Inc.", "Pas trouvé"], gorillaz.Tracks.Select(t => t.Title));
        Assert.Equal("https://i.ytimg.com/vi/HyHNuVaZJ-k/mqdefault.jpg", gorillaz.Tracks[0].ThumbnailUrl);
        Assert.Null(gorillaz.Tracks[1].VideoId);

        var user = await ClientAsync("disco-user", Roles.User);
        var contributor = await ClientAsync("disco-contrib", Roles.Contributor);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/discovery/artists", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await contributor.GetAsync("/api/discovery/artists", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.PostAsync("/api/admin/discovery/refresh", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/discovery/refresh", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_pre_downloaded_preview_is_served_first()
    {
        var dir = Path.Combine(factory.DataPath, "discovery", "previews");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "eeeeeeeeee1.mp3"), new byte[2048], Ct);

        var admin = await factory.AdminClientAsync();
        var response = await admin.GetAsync("/api/requests/preview/eeeeeeeeee1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2048, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task Discovery_settings_are_adjustable_by_the_admin_within_limits()
    {
        var admin = await factory.AdminClientAsync();
        var contributor = await ClientAsync("disco-settings", Roles.Contributor);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.GetAsync("/api/admin/discovery/settings", Ct)).StatusCode);
        Assert.Equal(DiscoverySettingsDto.Defaults, await admin.GetFromJsonAsync<DiscoverySettingsDto>("/api/admin/discovery/settings", Ct));
        Assert.Equal(new DiscoverySettingsDto(6, 6, 24, 7, 3), DiscoverySettingsDto.Defaults);

        foreach (var bad in new[] { new DiscoverySettingsDto(0, 6, 24, 7, 3), new DiscoverySettingsDto(6, -1, 24, 7, 3),
                     new DiscoverySettingsDto(6, 6, 1000, 7, 3), new DiscoverySettingsDto(6, 6, 24, 0, 3), new DiscoverySettingsDto(6, 6, 24, 7, 11) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/discovery/settings", bad, Ct)).StatusCode);

        try
        {
            var mine = new DiscoverySettingsDto(12, 4, 10, 14, 5);
            Assert.Equal(mine, await (await admin.PutAsJsonAsync("/api/admin/discovery/settings", mine, Ct)).Content.ReadFromJsonAsync<DiscoverySettingsDto>(Ct));
            Assert.Equal(mine, await admin.GetFromJsonAsync<DiscoverySettingsDto>("/api/admin/discovery/settings", Ct));
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/discovery/settings", DiscoverySettingsDto.Defaults, Ct);
        }
    }

    [Fact]
    public void The_age_limit_of_a_recommendation_follows_the_setting()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var plan = DiscoveryRanker.Plan([], [("a", now.AddDays(-8)), ("b", now.AddDays(-3))], now, newPerRun: 0, minShown: 0, maxAge: TimeSpan.FromDays(2));
        Assert.Equal(["a", "b"], plan.Expired);
    }

    private async Task<HttpClient> ClientAsync(string name, string role)
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", role), Ct)).EnsureSuccessStatusCode();
        return await factory.ClientAsync(name, $"{name}-password");
    }
}
