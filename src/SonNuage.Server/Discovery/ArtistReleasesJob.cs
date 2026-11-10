using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Nouveautés des artistes de la bibliothèque (couche 2 de la découverte), file « Métadonnées » (MusicBrainz : une
/// requête par seconde). Trois temps, bornés à chaque passage pour ne jamais surcharger MusicBrainz :
/// <list type="number">
/// <item>identifier les artistes sans identifiant sûr, d'après les crédits de leurs albums ou morceaux identifiés ;</item>
/// <item>lire les groupes de parutions des artistes dont le cache a expiré, les plus écoutés d'abord ;</item>
/// <item>une fois par jour, ajouter les « fresh releases » de ListenBrainz qui concernent nos artistes.</item>
/// </list>
/// </summary>
public sealed class ArtistReleasesJob(MusicBrainzClient musicBrainz, ListenBrainzClient listenBrainz, TimeProvider time) : IJobHandler
{
    /// <summary>Artistes à identifier par passage (deux requêtes chacun au plus).</summary>
    public const int DerivePerRun = 40;
    /// <summary>Artistes dont on relit les parutions par passage (une à trois requêtes chacun).</summary>
    public const int RefreshPerRun = 40;
    public const int MaxPages = 3;
    public const int CreditsPerArtist = 2;
    public const string FreshReleasesKey = "lb-fresh-releases";
    public const string ReleaseGroupsPrefix = "mb-release-groups:";
    /// <summary>Un artiste qu'on n'a pas su identifier n'est retenté qu'au bout de ce délai.</summary>
    public static readonly TimeSpan DeriveRetry = TimeSpan.FromDays(30);
    public static readonly TimeSpan FreshReleasesEvery = TimeSpan.FromDays(1);

    public JobKind Kind => JobKinds.ArtistReleases;

    public static JobPlan Plan() =>
        new JobPlan(JobSuiteKinds.ArtistReleases, Tx.Key("jobs.suites.releases")).Then(JobKinds.ArtistReleases, Tx.Key("jobs.suites.releases"));

    /// <summary>Artistes sans identifiant sûr, avec un album ou un morceau identifié, pas tentés depuis <see cref="DeriveRetry"/>.</summary>
    public static IQueryable<Artist> ToDerive(SonNuageDbContext db, DateTimeOffset now)
    {
        var before = now - DeriveRetry;
        return db.Artists.Where(a => a.MusicBrainzIdSource < ArtistMbidSource.Derived
            && (a.MusicBrainzDerivedAt == null || a.MusicBrainzDerivedAt < before)
            && (a.Albums.Any(al => al.MusicBrainzReleaseId != null) || a.Tracks.Any(t => t.MusicBrainzRecordingId != null)));
    }

    /// <summary>Artistes de la bibliothèque (avec un album) identifiés, dont les parutions n'ont jamais été lues ou ont expiré.</summary>
    public static IQueryable<Artist> ToRefresh(SonNuageDbContext db, DateTimeOffset now) =>
        db.Artists.Where(a => a.MusicBrainzId != null && a.MusicBrainzId != ReleaseRules.VariousArtistsMbid && a.Albums.Any()
            && !db.ExternalFetches.Any(f => f.Key == ReleaseGroupsPrefix + a.MusicBrainzId && f.ExpiresAt > now));

    /// <summary>Il y a quelque chose à faire (sert au planificateur).</summary>
    public static async Task<bool> PendingAsync(SonNuageDbContext db, DateTimeOffset now, CancellationToken ct) =>
        await ToDerive(db, now).AnyAsync(ct) || await ToRefresh(db, now).AnyAsync(ct);

    public async Task RunAsync(JobContext ctx)
    {
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var now = time.GetUtcNow();
        int derived = 0, refreshed = 0, fresh = 0;
        try
        {
            derived = await DeriveAsync(ctx, db, now);
            ctx.Report(0.4);
            refreshed = await RefreshAsync(ctx, db, now);
            ctx.Report(0.9);
            fresh = await MergeFreshReleasesAsync(db, now, ctx.CancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // MusicBrainz injoignable : ce qui est lu est gardé, le reste attendra le prochain passage.
            ctx.Warn(Tx.Key("jobs.releases.unreachable", ("message", ex.Message)));
        }
        ctx.Report(1);
        ctx.Info(Tx.Key("jobs.releases.done", ("derived", derived), ("artists", refreshed), ("fresh", fresh)));
    }

    /// <summary>Les plus écoutés (90 derniers jours, tous comptes) d'abord ; ensuite par nom.</summary>
    private static async Task<List<(Guid Id, int Plays)>> RankAsync(SonNuageDbContext db, IQueryable<Artist> artists, DateTimeOffset now, int take, CancellationToken ct)
    {
        var since = now.AddDays(-ReleaseRules.RecentDays);
        var rows = await artists
            .Select(a => new { a.Id, a.SortName, Plays = db.PlayEvents.Count(p => p.PlayedAt >= since && (p.Track.ArtistId == a.Id || p.Track.Album.ArtistId == a.Id)) })
            .OrderByDescending(x => x.Plays).ThenBy(x => x.SortName).Take(take).ToListAsync(ct);
        return rows.Select(x => (x.Id, x.Plays)).ToList();
    }

    private async Task<int> DeriveAsync(JobContext ctx, SonNuageDbContext db, DateTimeOffset now)
    {
        var ct = ctx.CancellationToken;
        var found = 0;
        foreach (var (id, _) in await RankAsync(db, ToDerive(db, now), now, DerivePerRun, ct))
        {
            var artist = await db.Artists.FirstAsync(a => a.Id == id, ct);
            var credits = new List<IReadOnlyList<MbArtistCredit>>();
            var albums = await db.Albums.Where(al => al.ArtistId == id && al.MusicBrainzReleaseId != null)
                .OrderBy(al => al.Title).Take(CreditsPerArtist).ToListAsync(ct);
            foreach (var album in albums)
            {
                if (await musicBrainz.ReleaseCreditsAsync(album.MusicBrainzReleaseId!.Value, ct) is not { } release) continue;
                credits.Add(release.Credits);
                album.MusicBrainzReleaseGroupId ??= release.ReleaseGroupId;
            }
            if (albums.Count == 0)
            {
                var recordings = await db.Tracks.Where(t => t.ArtistId == id && t.MusicBrainzRecordingId != null)
                    .Select(t => t.MusicBrainzRecordingId!.Value).Distinct().Take(CreditsPerArtist).ToListAsync(ct);
                foreach (var recording in recordings)
                {
                    if (await musicBrainz.RecordingCreditsAsync(recording, ct) is { } credit) credits.Add(credit);
                }
            }
            if (ArtistMbidDeriver.Pick(artist.Name, credits) is { } mbid && artist.OfferMusicBrainzId(mbid, ArtistMbidSource.Derived)) found++;
            artist.MusicBrainzDerivedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return found;
    }

    private async Task<int> RefreshAsync(JobContext ctx, SonNuageDbContext db, DateTimeOffset now)
    {
        var ct = ctx.CancellationToken;
        var ranked = await RankAsync(db, ToRefresh(db, now), now, RefreshPerRun, ct);
        var ids = ranked.Select(r => r.Id).ToList();
        var mbids = await db.Artists.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.MusicBrainzId!, ct);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, plays) in ranked)
        {
            // Deux artistes de la bibliothèque peuvent porter le même identifiant : une seule lecture.
            var mbid = mbids[id];
            if (!done.Add(mbid)) continue;
            var groups = await musicBrainz.ReleaseGroupsAsync(mbid, MaxPages, ct);
            await db.ArtistReleases.Where(r => r.ArtistMbid == mbid).ExecuteDeleteAsync(ct);
            db.ArtistReleases.AddRange(groups.Select(g => new ArtistRelease
            {
                ArtistMbid = mbid,
                ReleaseGroupId = g.Id,
                Title = Truncate(g.Title, 500),
                PrimaryType = g.PrimaryType is { Length: <= 32 } type ? type : null,
                SecondaryTypes = Truncate(string.Join(",", g.SecondaryTypes), 200),
                FirstReleaseDate = g.FirstReleaseDate is { Length: <= 10 } date ? date : null,
                ReleaseDate = ReleaseRules.ParseDate(g.FirstReleaseDate),
                FetchedAt = now,
            }));
            await MarkAsync(db, ReleaseGroupsPrefix + mbid, now, ReleaseRules.ExpiresAt(now, plays > 0, mbid), ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return done.Count;
    }

    /// <summary>« Fresh releases » de ListenBrainz (une requête par jour) : ajoute ce que MusicBrainz n'a pas encore donné pour nos artistes.</summary>
    private async Task<int> MergeFreshReleasesAsync(SonNuageDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.ExternalFetches.AnyAsync(f => f.Key == FreshReleasesKey && f.ExpiresAt > now, ct)) return 0;
        var ours = (await db.Artists.Where(a => a.MusicBrainzId != null && a.Albums.Any()).Select(a => a.MusicBrainzId!).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ours.Remove(ReleaseRules.VariousArtistsMbid);
        if (ours.Count == 0) return 0;

        var releases = await listenBrainz.FreshReleasesAsync(ct);
        var wanted = releases.SelectMany(r => r.ArtistMbids.Where(ours.Contains).Select(m => (Mbid: m.ToLowerInvariant(), Release: r))).ToList();
        var groups = wanted.Select(w => w.Release.ReleaseGroupId).Distinct().ToList();
        var known = (await db.ArtistReleases.Where(r => groups.Contains(r.ReleaseGroupId)).Select(r => new { r.ArtistMbid, r.ReleaseGroupId }).ToListAsync(ct))
            .Select(r => (r.ArtistMbid, r.ReleaseGroupId)).ToHashSet();
        var added = 0;
        foreach (var (mbid, release) in wanted)
        {
            if (!known.Add((mbid, release.ReleaseGroupId))) continue;
            db.ArtistReleases.Add(new ArtistRelease
            {
                ArtistMbid = mbid,
                ReleaseGroupId = release.ReleaseGroupId,
                Title = Truncate(release.Title, 500),
                PrimaryType = release.PrimaryType is { Length: <= 32 } type ? type : null,
                SecondaryTypes = Truncate(release.SecondaryType ?? "", 200),
                FirstReleaseDate = release.Date is { Length: <= 10 } date ? date : null,
                ReleaseDate = ReleaseRules.ParseDate(release.Date),
                FetchedAt = now,
            });
            added++;
        }
        await MarkAsync(db, FreshReleasesKey, now, now + FreshReleasesEvery, ct);
        await db.SaveChangesAsync(ct);
        return added;
    }

    private static async Task MarkAsync(SonNuageDbContext db, string key, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct)
    {
        var fetch = await db.ExternalFetches.FirstOrDefaultAsync(f => f.Key == key, ct);
        if (fetch is null) db.ExternalFetches.Add(fetch = new ExternalFetch { Key = key });
        fetch.FetchedAt = now;
        fetch.ExpiresAt = expires;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Demande la tâche des nouveautés quelques minutes après le démarrage puis toutes les 2 heures, s'il y a quelque chose à faire.</summary>
public sealed class ArtistReleasesScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<ArtistReleasesScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool pending;
                    using (var scope = scopes.CreateScope())
                        pending = await ArtistReleasesJob.PendingAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), time.GetUtcNow(), stoppingToken);
                    if (pending) await scopes.RequestAsync(ArtistReleasesJob.Plan(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Nouveautés des artistes non planifiées");
                }
                await Task.Delay(CheckEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
