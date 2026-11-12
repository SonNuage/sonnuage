using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Radio;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Faux Open-Meteo et faux MusicBrainz ; les stations acceptent ici les morceaux très courts de la bibliothèque de test.</summary>
public sealed class RadioFactory : SonNuageFactory
{
    public readonly FakeWeather Weather = new();
    public readonly FakeTags Tags = new();

    protected override void ConfigureMore(IWebHostBuilder builder) => builder.ConfigureServices(s =>
    {
        s.RemoveAll<IWeatherSource>();
        s.AddSingleton<IWeatherSource>(Weather);
        s.RemoveAll<IReleaseTagSource>();
        s.AddSingleton<IReleaseTagSource>(Tags);
        s.RemoveAll<RadioStations>();
        s.AddSingleton(sp => new RadioStations(sp.GetRequiredService<IMemoryCache>(), Weather, TimeProvider.System) { MinTracks = 1, MinDurationSeconds = 0 });
    });

    public sealed class FakeWeather : IWeatherSource
    {
        public WeatherNow? Now;
        public Task<WeatherNow?> NowAsync(CancellationToken ct) => Task.FromResult(Now);

        public Task<WeatherPlace?> FindPlaceAsync(string name, string language, CancellationToken ct) =>
            Task.FromResult(name.Trim().Equals("lyon", StringComparison.OrdinalIgnoreCase) ? new WeatherPlace("Lyon, France", 45.75, 4.85) : null);
    }

    public sealed class FakeTags : IReleaseTagSource
    {
        public Task<MbReleaseTags?> ReleaseTagsAsync(Guid releaseId, CancellationToken ct) => Task.FromResult<MbReleaseTags?>(
            new MbReleaseTags(new Dictionary<string, int> { ["christmas"] = 3, ["pop"] = 5 }, new Dictionary<Guid, IReadOnlyDictionary<string, int>>()));
    }
}

public sealed class RadioStationTests(RadioFactory factory) : IClassFixture<RadioFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Tous les morceaux de test « sonnent » joyeux, calmes et pop : assez pour la pop bonne humeur et la pluie.</summary>
    private async Task<HttpClient> SessionAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        foreach (var id in await db.Tracks.Select(t => t.Id).ToListAsync(Ct))
        {
            if (await db.AudioFeatures.AnyAsync(f => f.TrackId == id, Ct)) continue;
            db.AudioFeatures.Add(new TrackAudioFeatures
            {
                TrackId = id, Version = TrackAudioFeatures.CurrentVersion, AnalyzedAt = DateTimeOffset.UtcNow, Happy = 0.9, Valence = 0.9,
                Danceability = 0.8, Relaxed = 0.9, Acoustic = 0.9, Energy = 0.2, Sad = 0.5, Aggressive = 0.05, Styles = "Pop---Europop:0.6",
            });
        }
        await db.SaveChangesAsync(Ct);
        factory.Services.GetRequiredService<RadioStations>().Forget();
        return admin;
    }

    [Fact]
    public async Task Stations_are_listed_in_the_account_language_with_covers()
    {
        var client = await SessionAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/radio/stations");
        request.Headers.AcceptLanguage.ParseAdd("fr-FR");

        var stations = (await (await client.SendAsync(request, Ct)).Content.ReadFromJsonAsync<List<RadioStationDto>>(Ct))!;

        var pop = Assert.Single(stations, s => s.Id == "good-mood-pop");
        Assert.Equal("Pop bonne humeur", pop.Name);
        Assert.Equal(RadioStationKinds.Mood, pop.Kind);
        Assert.StartsWith("#", pop.Color);
        Assert.NotEmpty(pop.CoverAlbumIds);
        Assert.DoesNotContain(stations, s => s.Id == "energy");
    }

    [Fact]
    public async Task A_station_plays_its_tracks_without_the_excluded_ones()
    {
        var client = await SessionAsync();
        var all = await NextAsync(client, "good-mood-pop", [], 50);
        Assert.NotEmpty(all);

        var next = await NextAsync(client, "good-mood-pop", [all[0].Id], 50);

        Assert.DoesNotContain(next, t => t.Id == all[0].Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/radio/stations/nope/next", new StationNextRequest([], 5), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/radio/stations/energy/next", new StationNextRequest([], 5), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_weather_station_follows_the_weather_of_the_chosen_city()
    {
        var client = await SessionAsync();
        factory.Weather.Now = null;
        Assert.DoesNotContain(await StationsAsync(client), s => s.Kind == RadioStationKinds.Weather);

        factory.Weather.Now = new WeatherNow(61, 8.6, true, "Lyon");
        var weather = Assert.Single(await StationsAsync(client), s => s.Kind == RadioStationKinds.Weather);

        Assert.Equal("weather", weather.Id);
        Assert.Equal("Rain, 9 °C in Lyon", weather.Description);
        Assert.NotEmpty(await NextAsync(client, "weather", [], 10));
        factory.Weather.Now = null;
    }

    [Fact]
    public async Task The_admin_chooses_the_weather_city()
    {
        var admin = await SessionAsync();

        var saved = await (await admin.PutAsJsonAsync("/api/admin/radio", new RadioSettingsRequest(" lyon "), Ct)).Content.ReadFromJsonAsync<RadioSettingsDto>(Ct);
        Assert.Equal("Lyon, France", saved!.Place);
        Assert.Equal(45.75, saved.Latitude);
        Assert.Equal("Lyon, France", (await admin.GetFromJsonAsync<RadioSettingsDto>("/api/admin/radio", Ct))!.Place);

        var missing = await admin.PutAsJsonAsync("/api/admin/radio", new RadioSettingsRequest("Nowhere"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var cleared = await (await admin.PutAsJsonAsync("/api/admin/radio", new RadioSettingsRequest(""), Ct)).Content.ReadFromJsonAsync<RadioSettingsDto>(Ct);
        Assert.Null(cleared!.Place);
    }

    [Fact]
    public async Task Musicbrainz_tags_of_identified_albums_are_read_for_every_track()
    {
        var admin = await SessionAsync();
        Guid albumId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            albumId = await db.Albums.Where(a => a.Title == "First Album").Select(a => a.Id).FirstAsync(Ct);
            await db.Albums.Where(a => a.Id == albumId).ExecuteUpdateAsync(s => s.SetProperty(a => a.MusicBrainzReleaseId, Guid.NewGuid()), Ct);
        }

        var plan = RadioTagsJob.Plan();
        using (var scope = factory.Services.CreateScope())
            await factory.Services.GetRequiredService<JobEngine>().EnqueueAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), plan, Ct);
        await WaitAsync(admin, plan.Last.Id);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var tracks = await db.Tracks.Where(t => t.AlbumId == albumId).Select(t => new { t.Id, t.TagsFetchedAt }).ToListAsync(Ct);
            Assert.All(tracks, t => Assert.NotNull(t.TagsFetchedAt));
            var tags = await db.TrackTags.Where(t => t.TrackId == tracks[0].Id).Select(t => t.Name).ToListAsync(Ct);
            Assert.Contains("christmas", tags);
            Assert.False(await RadioTagsJob.Pending(db, DateTimeOffset.UtcNow).AnyAsync(a => a.Id == albumId, Ct));
        }
    }

    [Fact]
    public async Task Subsonic_apps_see_the_stations_as_read_only_playlists()
    {
        var admin = await SessionAsync();
        var access = (await (await admin.PostAsync("/api/me/subsonic", null, Ct)).Content.ReadFromJsonAsync<SubsonicAccessDto>(Ct))!;
        var client = factory.CreateClient();
        async Task<System.Text.Json.JsonElement> Call(string method, string query = "")
        {
            var text = await client.GetStringAsync($"/rest/{method}?apiKey={access.Password}&v=1.16.1&c=tests&f=json&{query}", Ct);
            return System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").Clone();
        }

        var playlists = (await Call("getPlaylists")).GetProperty("playlists").GetProperty("playlist").EnumerateArray().ToList();
        var radio = Assert.Single(playlists, p => p.GetProperty("name").GetString() == "Good mood pop radio");
        Assert.Equal("SonNuage", radio.GetProperty("owner").GetString());

        var playlist = (await Call("getPlaylist", $"id={radio.GetProperty("id").GetString()}")).GetProperty("playlist");
        Assert.NotEmpty(playlist.GetProperty("entry").EnumerateArray());
        Assert.Equal("failed", (await Call("deletePlaylist", $"id={radio.GetProperty("id").GetString()}")).GetProperty("status").GetString());
    }

    private async Task<List<RadioStationDto>> StationsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<RadioStationDto>>("/api/radio/stations", Ct))!;

    private async Task<List<TrackDto>> NextAsync(HttpClient client, string id, IReadOnlyList<Guid> exclude, int count)
    {
        var response = await client.PostAsJsonAsync($"/api/radio/stations/{id}/next", new StationNextRequest(exclude, count), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<TrackDto>>(Ct))!;
    }

    private async Task WaitAsync(HttpClient admin, Guid jobId)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = (await admin.GetFromJsonAsync<JobDetailDto>($"/api/admin/jobs/{jobId}", Ct))!.Job;
            if (job.Status == "Succeeded") return;
            if (job.Status is "Failed" or "Canceled" || watch.Elapsed > TimeSpan.FromSeconds(20)) throw new Exception($"{job.Title} : {job.Status}");
            await Task.Delay(50, Ct);
        }
    }
}
