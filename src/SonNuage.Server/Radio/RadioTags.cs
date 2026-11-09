using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Radio;

/// <summary>Tags MusicBrainz d'une édition (voir <see cref="MusicBrainzClient.ReleaseTagsAsync"/>).</summary>
public interface IReleaseTagSource
{
    Task<MbReleaseTags?> ReleaseTagsAsync(Guid releaseId, CancellationToken ct);
}

/// <summary>
/// Lit les tags MusicBrainz des albums identifiés (une requête par album) : chaque morceau reçoit ceux de son
/// enregistrement, de son édition et du groupe d'éditions (« christmas », « summer », genres…). Relu tous les 90 jours.
/// </summary>
public sealed class RadioTagsJob : IJobHandler
{
    public const int AlbumsPerRun = 300;
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(90);

    public JobKind Kind => JobKinds.RadioTags;

    public static JobPlan Plan() =>
        new JobPlan(JobSuiteKinds.RadioTags, Tx.Key("radio.tags.suite")).Then(JobKinds.RadioTags, Tx.Key("radio.tags.suite"));

    /// <summary>Albums identifiés dont un morceau n'a pas de tags récents.</summary>
    public static IQueryable<Album> Pending(SonNuageDbContext db, DateTimeOffset now)
    {
        var before = now - RefreshAfter;
        return db.Albums.Where(a => a.MusicBrainzReleaseId != null
            && a.Tracks.Any(t => t.TagsFetchedAt == null || t.TagsFetchedAt < before));
    }

    public async Task RunAsync(JobContext ctx)
    {
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var source = ctx.Services.GetRequiredService<IReleaseTagSource>();
        var now = ctx.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var ct = ctx.CancellationToken;
        var albums = await Pending(db, now).OrderBy(a => a.Id).Select(a => new { a.Id, ReleaseId = a.MusicBrainzReleaseId!.Value })
            .Take(AlbumsPerRun).ToListAsync(ct);
        int done = 0, tagged = 0;
        foreach (var album in albums)
        {
            var tags = await source.ReleaseTagsAsync(album.ReleaseId, ct);
            var tracks = await db.Tracks.Where(t => t.AlbumId == album.Id).ToListAsync(ct);
            var ids = tracks.Select(t => t.Id).ToList();
            await db.TrackTags.Where(t => ids.Contains(t.TrackId)).ExecuteDeleteAsync(ct);
            foreach (var track in tracks)
            {
                track.TagsFetchedAt = now;
                if (tags is null) continue;
                var merged = new Dictionary<string, int>(tags.Release);
                if (track.MusicBrainzRecordingId is { } rec && tags.ByRecording.TryGetValue(rec, out var own))
                    foreach (var (name, count) in own) merged[name] = Math.Max(count, merged.GetValueOrDefault(name));
                db.TrackTags.AddRange(merged.Select(kv => new TrackTag { TrackId = track.Id, Name = kv.Key, Count = kv.Value }));
                if (merged.Count > 0) tagged++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            ctx.Report((double)++done / albums.Count);
        }
        if (albums.Count > 0) ctx.Services.GetRequiredService<RadioStations>().Forget();
        ctx.Info(Tx.Key("radio.tags.done", ("albums", done), ("tracks", tagged)));
    }
}

/// <summary>Demande la lecture des tags quelques minutes après le démarrage puis toutes les 12 heures, s'il y a des albums à lire.</summary>
public sealed class RadioTagsScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<RadioTagsScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(scopes, time.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Lecture des tags MusicBrainz non planifiée");
                }
                await Task.Delay(CheckEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static async Task CheckAsync(IServiceScopeFactory scopes, DateTimeOffset now, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            if (!await RadioTagsJob.Pending(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), now).AnyAsync(ct)) return;
        }
        await scopes.RequestAsync(RadioTagsJob.Plan(), ct);
    }
}
