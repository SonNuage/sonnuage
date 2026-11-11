using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Analysis;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Tests;

/// <summary>Analyseur factice : « Encore » est illisible, les autres morceaux sont des tubes à 120 BPM.</summary>
public sealed class AudioAnalysisFactory : SonNuageFactory
{
    public readonly ConcurrentQueue<string> Analyzed = new();

    public AudioAnalysisFactory()
    {
        JobEngine.PollInterval = TimeSpan.FromMilliseconds(200);
        // Trois morceaux, deux par tâche : la suite enchaîne une seconde tâche.
        AudioAnalysisJob.BatchSize = 2;
    }

    protected override void ConfigureMore(IWebHostBuilder builder) =>
        builder.ConfigureServices(s => s.AddSingleton<IAudioAnalyzer>(new Fake(this)));

    private sealed class Fake(AudioAnalysisFactory owner) : IAudioAnalyzer
    {
        public bool Available => true;

        public Task<AudioAnalysisOutcome> AnalyzeAsync(string path, CancellationToken ct)
        {
            owner.Analyzed.Enqueue(Path.GetFileName(path));
            AudioAnalysisOutcome outcome = Path.GetFileName(path) == "Encore.wav"
                ? new AudioAnalysisOutcome.Failed("RuntimeError: unreadable")
                : new AudioAnalysisOutcome.Analyzed(new AudioFeatureValues(Bpm: 120, Danceability: 0.9, Energy: 0.8, Valence: 0.7,
                    Happy: 0.6, Sad: 0.1, Relaxed: 0.2, Aggressive: 0.3, Party: 0.5, Acoustic: 0.4, Electronic: 0.35, Instrumental: 0.05));
            return Task.FromResult(outcome);
        }
    }
}

public sealed class AudioAnalysisTests(AudioAnalysisFactory factory) : IClassFixture<AudioAnalysisFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Attend que l'analyse n'ait plus rien à faire (et plus aucune tâche en cours).</summary>
    private static async Task<AudioAnalysisStatusDto> IdleAsync(HttpClient admin, int pending = 0)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var status = (await admin.GetFromJsonAsync<AudioAnalysisStatusDto>("/api/admin/analysis", Ct))!;
            if (status.Pending == pending && !status.Running) return status;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"Analyse : {status}");
            await Task.Delay(50, Ct);
        }
    }

    private async Task<HttpClient> ReadyAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        await IdleAsync(admin);
        return admin;
    }

    private async Task<T> DbAsync<T>(Func<SonNuageDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>());
    }

    [Fact]
    public async Task Every_track_is_analyzed_after_the_scan_and_failures_keep_their_error()
    {
        var admin = await ReadyAsync();

        var status = await IdleAsync(admin);
        Assert.Equal(new AudioAnalysisStatusDto(true, true, 3, 2, 1, 0, false), status);

        var rows = await DbAsync(db => db.AudioFeatures.Include(f => f.Track).AsNoTracking().ToListAsync(Ct));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(TrackAudioFeatures.CurrentVersion, r.Version));
        Assert.All(rows, r => Assert.Equal(r.Track.FileModifiedUtc, r.FileModifiedUtc));
        var failed = Assert.Single(rows, r => r.Error is not null);
        Assert.Equal("Encore", failed.Track.Title);
        Assert.Equal("RuntimeError: unreadable", failed.Error);
        Assert.Null(failed.Bpm);
        var hit = Assert.Single(rows, r => r.Track.Title == "Opening");
        Assert.Equal((120, 0.9, 0.8, 0.7, 0.6, 0.1, 0.2, 0.3, 0.5, 0.4, 0.35, 0.05),
            (hit.Bpm, hit.Danceability, hit.Energy, hit.Valence, hit.Happy, hit.Sad, hit.Relaxed, hit.Aggressive, hit.Party, hit.Acoustic, hit.Electronic, hit.Instrumental));
        Assert.Null(hit.Error);
    }

    [Fact]
    public async Task Tracks_are_analyzed_in_batches_chained_in_one_suite()
    {
        await ReadyAsync();

        var first = await DbAsync(db => db.JobSuites.Where(s => s.Kind == JobSuiteKinds.AudioAnalysis).OrderBy(s => s.CreatedAt)
            .Select(s => s.Jobs.Count).FirstAsync(Ct));
        Assert.Equal(2, first);
    }

    [Fact]
    public async Task Only_new_outdated_or_changed_tracks_are_analyzed_again()
    {
        var admin = await ReadyAsync();
        var before = factory.Analyzed.Count;

        // Rien à refaire : ni les morceaux analysés, ni celui en échec.
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/analysis/run", null, Ct)).StatusCode);
        await IdleAsync(admin);
        Assert.Equal(before, factory.Analyzed.Count);

        // Version de calcul dépassée pour l'un, fichier remplacé pour l'autre, morceau en échec dont le fichier a changé.
        await DbAsync(db => db.AudioFeatures.Where(f => f.Track.Title == "Opening").ExecuteUpdateAsync(s => s.SetProperty(f => f.Version, 0), Ct));
        await DbAsync(db => db.AudioFeatures.Where(f => f.Track.Title == "Second Song")
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.FileModifiedUtc, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct));
        Assert.Equal(2, (await admin.GetFromJsonAsync<AudioAnalysisStatusDto>("/api/admin/analysis", Ct))!.Pending);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/analysis/run", null, Ct)).StatusCode);
        await IdleAsync(admin);

        Assert.Equal(["01 - Opening.wav", "02_Second_Song.wav"], factory.Analyzed.Skip(before).Order());
        Assert.Equal(0, await DbAsync(db => db.AudioFeatures.CountAsync(f => f.Version != TrackAudioFeatures.CurrentVersion, Ct)));
    }

    [Fact]
    public async Task Tracks_just_added_go_first_then_the_backlog_whatever_its_age()
    {
        var admin = await ReadyAsync();
        var before = factory.Analyzed.Count;

        // Tout est à refaire. « Encore » vient d'arriver ; les deux autres sont anciens, et celui qui passe en premier
        // (le plus petit identifiant) est le plus ancien : la date d'ajout ne range pas l'arriéré.
        var old = (await DbAsync(db => db.Tracks.Where(t => t.Title != "Encore").Select(t => new { t.Id, t.FilePath }).ToListAsync(Ct)))
            .OrderBy(t => t.Id.ToString(), StringComparer.Ordinal).ToList();
        var now = DateTimeOffset.UtcNow;
        await DbAsync(db => db.Tracks.Where(t => t.Id == old[0].Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, now.AddDays(-400)), Ct));
        await DbAsync(db => db.Tracks.Where(t => t.Id == old[1].Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, now.AddDays(-30)), Ct));
        await DbAsync(db => db.AudioFeatures.ExecuteUpdateAsync(s => s.SetProperty(f => f.Version, 0), Ct));

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/analysis/run", null, Ct)).StatusCode);
        await IdleAsync(admin);

        Assert.Equal(["Encore.wav", Path.GetFileName(old[0].FilePath), Path.GetFileName(old[1].FilePath)], factory.Analyzed.Skip(before));
    }

    [Fact]
    public async Task Disabling_the_analysis_stops_it_until_enabled_again()
    {
        var admin = await ReadyAsync();
        var before = factory.Analyzed.Count;
        try
        {
            var disabled = await (await admin.PutAsJsonAsync("/api/admin/analysis/settings", new AudioAnalysisSettingsDto(false), Ct))
                .Content.ReadFromJsonAsync<AudioAnalysisStatusDto>(Ct);
            Assert.False(disabled!.Enabled);
            await DbAsync(db => db.AudioFeatures.Where(f => f.Track.Title == "Opening").ExecuteDeleteAsync(Ct));

            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/analysis/run", null, Ct)).StatusCode);
            await Task.Delay(500, Ct);
            Assert.Equal(before, factory.Analyzed.Count);
            Assert.Equal(1, (await admin.GetFromJsonAsync<AudioAnalysisStatusDto>("/api/admin/analysis", Ct))!.Pending);
        }
        finally
        {
            // Réactivée : l'analyse reprend d'elle-même.
            await admin.PutAsJsonAsync("/api/admin/analysis/settings", new AudioAnalysisSettingsDto(true), Ct);
        }
        await IdleAsync(admin);
        Assert.Equal(["01 - Opening.wav"], factory.Analyzed.Skip(before));
    }

    [Fact]
    public async Task Analysis_status_is_for_admins_only()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/analysis", Ct)).StatusCode);
    }
}

/// <summary>Lignes échangées avec le script Python : lues une fois, à l'entrée, puis crues.</summary>
public sealed class EssentiaProtocolTests
{
    [Fact]
    public void Ready_and_fatal_lines_announce_the_start_of_the_analyzer()
    {
        Assert.Equal(new EssentiaProtocol.Ready(1), EssentiaProtocol.Parse("""{"ready": true, "version": 1}"""));
        Assert.Equal(new EssentiaProtocol.Fatal("FileNotFoundError: model missing"), EssentiaProtocol.Parse("""{"fatal": "FileNotFoundError: model missing"}"""));
    }

    [Fact]
    public void A_result_line_carries_the_features_of_the_file()
    {
        var line = """
            {"id": "a1", "seconds": 4.2, "features": {"happy": 0.61, "sad": 0.1, "relaxed": 0.2, "aggressive": 0.3, "party": 0.5,
             "acoustic": 0.4, "electronic": 0.35, "danceability": 0.9, "instrumental": 0.05, "valence": 0.7, "energy": 0.8, "bpm": 123.5}}
            """.ReplaceLineEndings(" ");

        var result = Assert.IsType<EssentiaProtocol.Result>(EssentiaProtocol.Parse(line));

        Assert.Equal("a1", result.Id);
        Assert.Equal(new AudioAnalysisOutcome.Analyzed(new AudioFeatureValues(123.5, 0.9, 0.8, 0.7, 0.61, 0.1, 0.2, 0.3, 0.5, 0.4, 0.35, 0.05)), result.Outcome);
    }

    [Fact]
    public void Missing_or_out_of_range_values_are_left_empty()
    {
        var result = Assert.IsType<EssentiaProtocol.Result>(EssentiaProtocol.Parse("""{"id": "b", "features": {"happy": 1.5, "sad": -0.1, "bpm": null, "party": 1}}"""));

        var values = Assert.IsType<AudioAnalysisOutcome.Analyzed>(result.Outcome).Values;
        Assert.Equal(new AudioFeatureValues(null, null, null, null, null, null, null, null, 1, null, null, null), values);
    }

    [Fact]
    public void An_error_line_is_the_failure_of_that_file()
    {
        var result = Assert.IsType<EssentiaProtocol.Result>(EssentiaProtocol.Parse("""{"id": "c", "error": "RuntimeError: cannot decode"}"""));
        Assert.Equal(new EssentiaProtocol.Result("c", new AudioAnalysisOutcome.Failed("RuntimeError: cannot decode")), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-11-09 tensorflow warning")]
    [InlineData("[1, 2]")]
    [InlineData("""{"something": "else"}""")]
    public void Anything_else_is_noise(string line) => Assert.Equal(EssentiaProtocol.Noise, EssentiaProtocol.Parse(line));
}

/// <summary>Sans analyseur installé (Windows, arm64…) : rien ne tourne, l'admin le dit.</summary>
public sealed class AudioAnalysisUnavailableTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_analyzer_the_analysis_is_unavailable_and_cannot_be_started()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);

        var status = await admin.GetFromJsonAsync<AudioAnalysisStatusDto>("/api/admin/analysis", Ct);
        Assert.Equal(new AudioAnalysisStatusDto(false, false, 3, 0, 0, 3, false), status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/analysis/run", null, Ct)).StatusCode);
    }
}
