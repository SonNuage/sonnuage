using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Storage;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Actualise la découverte par artiste (tâche « discovery.refresh ») :
/// 1. les artistes les plus écoutés de la bibliothèque reçoivent leur identifiant MusicBrainz ;
/// 2. ListenBrainz donne leurs artistes proches, classés et enregistrés en base (sans ceux qu'on a déjà) ;
/// 3. leurs titres phares (ListenBrainz, sinon Deezer) sont retrouvés sur YouTube par la recherche par correspondance ;
/// 4. un extrait de 30 s est pré-téléchargé pour chacun, dans <c>var/discovery/previews</c>, tant que la place
///    allouée aux extraits et l'espace libre minimum le permettent.
/// Chaque étape garde ce qui est déjà fait : une actualisation interrompue reprend où elle en était.
/// </summary>
public sealed class DiscoveryService(
    ListenBrainzClient listenBrainz,
    DeezerClient deezer,
    MusicBrainzClient musicBrainz,
    YtDlp ytDlp,
    YoutubeTools tools,
    Streaming.FfmpegLocator ffmpeg,
    PreviewService previews,
    StorageGuard storage,
    DiscoverySettingsStore settings,
    IOptions<SonNuageOptions> options,
    TimeProvider time,
    ILogger<DiscoveryService> logger)
{
    public const int SeedCount = 12;
    public const int SimilarPerSeed = 25;
    /// <summary>Candidats classés à chaque actualisation, parmi lesquels on prend les nouveaux.</summary>
    public const int CandidatePool = 200;

    /// <summary>
    /// Titres phares gardés par artiste. Les premiers (réglage « titres par artiste ») sont cherchés sur YouTube à
    /// l'actualisation ; les suivants seulement quand quelqu'un les demande (« voir plus »).
    /// </summary>
    public const int StoredTracks = 15;

    public const int MinMatchScore = 60;
    private const int PrefetchPerRun = 40;
    private static readonly TimeSpan MbidRetry = TimeSpan.FromDays(30);
    private static readonly TimeSpan TracksRefresh = TimeSpan.FromDays(30);
    /// <summary>Taille approximative d'un extrait (30 s en MP3 ~ 130 kbit/s), pour ne pas dépasser la place allouée.</summary>
    private const long PreviewSize = 600 * 1024;

    private string PreviewDir => options.Value.DiscoveryPreviewPath;

    public async Task RefreshAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();

        var seeds = await SeedsAsync(db, ctx, ct);
        ctx.Report(0.3);
        var ranked = DiscoveryRanker.Rank(seeds,
            await db.Artists.Where(a => a.MusicBrainzId != null).Select(a => a.MusicBrainzId!).ToListAsync(ct),
            await db.Artists.Select(a => a.Name).ToListAsync(ct), CandidatePool);
        var update = await SaveArtistsAsync(db, ranked, ct);
        ctx.Info(Tx.Key("jobs.discovery.updated", ("seeds", seeds.Count), ("added", update.Added.Count), ("expired", update.Expired.Count),
            ("count", await db.DiscoveryArtists.CountAsync(ct))));
        ctx.Report(0.4);

        await FetchTracksAsync(db, ctx, ct);
        ctx.Report(0.55);
        await MatchAsync(db, ctx, ct);
        ctx.Report(0.8);
        await PrefetchAsync(db, ctx, ct);
        CleanOrphans(db);
        ctx.Report(1);
    }

    /// <summary>Artistes les plus écoutés (à défaut, ceux qui ont le plus de titres), avec leurs artistes proches.</summary>
    private async Task<List<DiscoverySeed>> SeedsAsync(SonNuageDbContext db, JobContext ctx, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-365);
        var top = await db.Artists
            .Where(a => a.Tracks.Any())
            .Select(a => new { Artist = a, Plays = db.PlayEvents.Count(p => p.Track.ArtistId == a.Id && p.PlayedAt >= since), Tracks = a.Tracks.Count })
            .OrderByDescending(x => x.Plays).ThenByDescending(x => x.Tracks)
            .Take(SeedCount)
            .ToListAsync(ct);

        var seeds = new List<DiscoverySeed>();
        for (var i = 0; i < top.Count; i++)
        {
            var artist = top[i].Artist;
            try
            {
                if (artist.MusicBrainzId is null && (artist.MusicBrainzCheckedAt is null || artist.MusicBrainzCheckedAt < time.GetUtcNow() - MbidRetry))
                {
                    if (Library.MusicBrainzId.Parse(await musicBrainz.ArtistIdAsync(artist.Name, ct)) is { } found)
                        artist.OfferMusicBrainzId(found, ArtistMbidSource.Search);
                    artist.MusicBrainzCheckedAt = time.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                }
                if (artist.MusicBrainzId is null) continue;
                var similar = (await listenBrainz.SimilarArtistsAsync(artist.MusicBrainzId, ct)).Take(SimilarPerSeed).ToList();
                // Le plus écouté pèse 1, le dernier de la liste un peu plus de la moitié.
                seeds.Add(new DiscoverySeed(artist.Name, 1 - 0.5 * i / Math.Max(top.Count, 1), similar));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                ctx.Warn(Tx.Key("jobs.discovery.sourceFailed", ("artist", artist.Name), ("message", ex.Message)));
            }
        }
        return seeds;
    }

    /// <summary>
    /// Actualisation incrémentale (<see cref="DiscoveryRanker.Plan"/>) : retire les propositions de plus de 7 jours,
    /// ajoute les nouvelles, et met à jour la proximité de celles qui restent.
    /// </summary>
    private async Task<DiscoveryUpdate> SaveArtistsAsync(SonNuageDbContext db, IReadOnlyList<RankedArtist> ranked, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var existing = await db.DiscoveryArtists.Include(a => a.Tracks).ToListAsync(ct);
        var s = settings.Settings;
        var update = DiscoveryRanker.Plan(ranked, existing.Select(a => (a.MusicBrainzId, a.AddedAt)).ToList(), now, s.NewPerRun, s.MinShown,
            TimeSpan.FromDays(s.MaxAgeDays));
        var expired = update.Expired.ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.DiscoveryArtists.RemoveRange(existing.Where(a => expired.Contains(a.MusicBrainzId)));

        var byId = ranked.ToDictionary(r => r.MusicBrainzId, StringComparer.OrdinalIgnoreCase);
        foreach (var artist in existing.Where(a => !expired.Contains(a.MusicBrainzId)))
        {
            if (byId.TryGetValue(artist.MusicBrainzId, out var r)) Apply(artist, r);
            artist.UpdatedAt = now;
        }
        foreach (var r in update.Added)
        {
            var artist = new DiscoveryArtist { MusicBrainzId = r.MusicBrainzId, Name = r.Name, AddedAt = now, UpdatedAt = now };
            Apply(artist, r);
            db.DiscoveryArtists.Add(artist);
        }
        await db.SaveChangesAsync(ct);
        return update;

        static void Apply(DiscoveryArtist artist, RankedArtist r)
        {
            artist.Name = r.Name;
            artist.Comment = r.Comment;
            artist.Score = r.Score;
            artist.BecauseOf = string.Join('\n', r.BecauseOf.Take(5));
        }
    }

    private async Task FetchTracksAsync(SonNuageDbContext db, JobContext ctx, CancellationToken ct)
    {
        var stale = time.GetUtcNow() - TracksRefresh;
        // « Pas plus de titres que ceux affichés » : artiste lu avant qu'on garde les suivants ; on le relit.
        var shown = settings.Settings.TracksPerArtist;
        var artists = await db.DiscoveryArtists.Include(a => a.Tracks)
            .Where(a => a.TracksFetchedAt == null || a.TracksFetchedAt < stale || a.Tracks.Count <= shown)
            .OrderByDescending(a => a.Score).ToListAsync(ct);
        foreach (var artist in artists)
        {
            try
            {
                var found = await listenBrainz.TopRecordingsAsync(artist.MusicBrainzId, ct);
                if (found.Count == 0) found = await deezer.TopTracksAsync(artist.Name, ct);
                var top = found
                    .DistinctBy(r => Library.NameNormalizer.Key(r.Title)).Take(Math.Max(StoredTracks, shown)).ToList();
                var previous = artist.Tracks.GroupBy(t => Library.NameNormalizer.Key(t.Title)).ToDictionary(g => g.Key, g => g.First());
                db.DiscoveryTracks.RemoveRange(artist.Tracks.Where(t => !top.Any(r => Library.NameNormalizer.Key(r.Title) == Library.NameNormalizer.Key(t.Title))));
                for (var i = 0; i < top.Count; i++)
                {
                    var r = top[i];
                    if (!previous.TryGetValue(Library.NameNormalizer.Key(r.Title), out var track))
                    {
                        track = new DiscoveryTrack { Title = r.Title, ArtistId = artist.Id };
                        db.DiscoveryTracks.Add(track);
                    }
                    track.Rank = i;
                    track.Title = r.Title;
                    track.RecordingMbid = r.RecordingMbid;
                    track.ReleaseName = r.ReleaseName;
                    track.DurationSeconds = r.LengthSeconds;
                }
                artist.TracksFetchedAt = time.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                ctx.Warn(Tx.Key("jobs.discovery.sourceFailed", ("artist", artist.Name), ("message", ex.Message)));
            }
        }
    }

    /// <summary>
    /// Cherche maintenant sur YouTube ceux de ces titres qui ne l'ont pas encore été (« voir plus » d'un artiste) ;
    /// s'arrête si YouTube bride. Sans yt-dlp, ils restent à chercher. Les titres viennent avec leur artiste.
    /// </summary>
    public async Task MatchAsync(SonNuageDbContext db, IEnumerable<DiscoveryTrack> tracks, CancellationToken ct)
    {
        if (tools.YtDlp is null) return;
        foreach (var track in tracks.Where(t => t.MatchedAt is null))
        {
            try
            {
                await MatchOneAsync(db, track, ct);
            }
            catch (YtDlpException ex)
            {
                logger.LogInformation("Recherche YouTube de « {Title} » impossible : {Message}", track.Title, ex.Message);
                if (ex.Kind == YtDlpFailure.Throttled) return;
            }
        }
    }

    /// <summary>Cherche le titre sur YouTube et note le résultat, trouvé ou non. Vrai s'il est trouvé.</summary>
    private async Task<bool> MatchOneAsync(SonNuageDbContext db, DiscoveryTrack track, CancellationToken ct)
    {
        // YouTube Music et YouTube, comme la recherche par correspondance des demandes : la recherche
        // YouTube Music ne donne ni chaîne ni durée, YouTube apporte la chaîne officielle et la durée.
        var best = (await ytDlp.MatchAsync(new MatchQuery(track.Artist.Name, track.Title, track.DurationSeconds),
            options.Value.MatchOfficialMinScore, ct)).FirstOrDefault();
        var found = best is { Score: >= MinMatchScore };
        if (found)
        {
            track.VideoId = best!.Entry.Id;
            track.VideoTitle = best.Entry.Title.Length > 500 ? best.Entry.Title[..500] : best.Entry.Title;
            track.Channel = best.Entry.Channel is { Length: > 200 } c ? c[..200] : best.Entry.Channel;
            track.MatchScore = best.Score;
        }
        else
        {
            track.VideoId = track.VideoTitle = track.Channel = null;
            track.MatchScore = null;
        }
        track.MatchedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return found;
    }

    /// <summary>
    /// Retrouve sur YouTube Music (recherche par correspondance) les premiers titres de chaque artiste, ceux que la
    /// page affiche d'emblée ; s'arrête si YouTube bride.
    /// </summary>
    private async Task MatchAsync(SonNuageDbContext db, JobContext ctx, CancellationToken ct)
    {
        var shown = settings.Settings.TracksPerArtist;
        var tracks = await db.DiscoveryTracks.Include(t => t.Artist)
            .Where(t => t.MatchedAt == null && t.Rank < shown)
            .OrderByDescending(t => t.Artist.Score).ThenBy(t => t.Rank)
            .ToListAsync(ct);
        if (tracks.Count == 0) return;
        if (tools.YtDlp is null)
        {
            ctx.Warn(Tx.Key("errors.yt.missing"));
            return;
        }
        var found = 0;
        foreach (var track in tracks)
        {
            try
            {
                if (await MatchOneAsync(db, track, ct)) found++;
            }
            catch (YtDlpException ex) when (ex.Kind == YtDlpFailure.Throttled)
            {
                ctx.Warn(Tx.Key("jobs.discovery.throttled"));
                break;
            }
            catch (YtDlpException ex)
            {
                ctx.Warn(Tx.Key("jobs.discovery.matchFailed", ("title", track.Title), ("message", ex.Message)));
            }
        }
        ctx.Info(Tx.Key("jobs.discovery.matched", ("found", found), ("count", tracks.Count)));
    }

    /// <summary>Pré-télécharge les extraits manquants, des artistes les plus proches aux moins proches, dans la limite de la place.</summary>
    private async Task PrefetchAsync(SonNuageDbContext db, JobContext ctx, CancellationToken ct)
    {
        var tracks = await db.DiscoveryTracks
            .Where(t => t.VideoId != null)
            .OrderByDescending(t => t.Artist.Score).ThenBy(t => t.Rank)
            .Select(t => new { t.VideoId, t.DurationSeconds })
            .ToListAsync(ct);
        if (tools.YtDlp is null || !ffmpeg.Available) return;
        Directory.CreateDirectory(PreviewDir);
        var made = 0;
        foreach (var t in tracks.DistinctBy(t => t.VideoId))
        {
            var path = Path.Combine(PreviewDir, $"{t.VideoId}.mp3");
            if (File.Exists(path)) continue;
            if (made >= PrefetchPerRun) break;
            if (storage.DownloadsBlocked)
            {
                ctx.Warn(storage.LowSpaceMessage());
                break;
            }
            if (previews.TotalBytes() + PreviewSize > storage.PreviewQuotaBytes)
            {
                ctx.Warn(Tx.Key("jobs.discovery.quotaReached"));
                break;
            }
            var work = Path.Combine(PreviewDir, "work-" + Guid.NewGuid().ToString("N"));
            try
            {
                var produced = await ytDlp.PreviewAsync(t.VideoId!, PreviewService.StartFor(t.DurationSeconds), PreviewService.Seconds, work, ct);
                File.Move(produced, path, overwrite: true);
                made++;
            }
            catch (YtDlpException ex) when (ex.Kind == YtDlpFailure.Throttled)
            {
                ctx.Warn(Tx.Key("jobs.discovery.throttled"));
                break;
            }
            catch (YtDlpException ex)
            {
                logger.LogInformation("Extrait de découverte {VideoId} impossible : {Message}", t.VideoId, ex.Message);
            }
            finally
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
        }
        ctx.Info(Tx.Key("jobs.discovery.prefetched", ("count", made)));
    }

    /// <summary>Supprime les extraits des titres qui ne sont plus proposés.</summary>
    private void CleanOrphans(SonNuageDbContext db)
    {
        if (!Directory.Exists(PreviewDir)) return;
        var used = db.DiscoveryTracks.Where(t => t.VideoId != null).Select(t => t.VideoId!).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(PreviewDir, "*.mp3"))
        {
            if (used.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
