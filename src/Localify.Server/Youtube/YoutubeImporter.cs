using Localify.Contracts.Localization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Localify.Contracts;
using Localify.Server.Data;
using Localify.Server.Jobs;
using Localify.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Youtube;

/// <summary>
/// Import YouTube, sous forme de suites de tâches (voir <see cref="JobEngine"/>) : téléchargement et conversion
/// (plusieurs en parallèle, réglage ParallelDownloads : une vidéo bridée ne bloque pas les autres) → empreinte
/// AcoustID (une à la fois, tous imports et rescans confondus) → métadonnées, tags, pochette, rangement, indexation.
/// Quand YouTube refuse (trop de requêtes, vérification anti-robot), la file se met en pause et reprend
/// seule, avec une attente qui double à chaque refus (30 min → 12 h) ; l'admin peut la relancer.
/// </summary>
public sealed class YoutubeImporter(
    IServiceScopeFactory scopes,
    YtDlp ytDlp,
    MetadataResolver resolver,
    YoutubeLibraryWriter writer,
    YoutubeSettingsStore settings,
    ScanCoordinator scans,
    IOptions<LocalifyOptions> options,
    TimeProvider time,
    ILogger<YoutubeImporter> logger) : BackgroundService
{
    private static readonly TimeSpan FirstHold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxHold = TimeSpan.FromHours(12);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, YoutubeProgressDto> _active = new();
    private readonly Lock _holdLock = new();
    private TimeSpan _nextHold = FirstHold;

    public DateTimeOffset? HoldUntil { get; private set; }
    public string? HoldReason { get; private set; }
    /// <summary>Imports en cours : traitement des métadonnées d'abord, puis téléchargements.</summary>
    public IReadOnlyList<YoutubeProgressDto> Active =>
        _active.Values.OrderBy(p => p.Stage == "download" ? 1 : 0).ThenBy(p => p.Title, StringComparer.CurrentCulture).ToList();

    public void Wake() => _wake.Writer.TryWrite(true);

    // Format yt-dlp choisi par l'admin pour la prochaine tentative d'un import (une seule fois, non conservé).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _formatOverrides = new();

    public void UseFormatOnce(Guid itemId, string format) => _formatOverrides[itemId] = format;

    /// <summary>Formats proposés par YouTube, relevés lors du dernier échec « format indisponible ».</summary>
    public string FormatsPath(string videoId) => Path.Combine(options.Value.YoutubeDataPath, "formats", $"{videoId}.txt");

    /// <summary>Fin de la pause : les téléchargements mis en attente repartent tout de suite.</summary>
    public async Task ResumeAsync(LocalifyDbContext db, JobEngine engine, CancellationToken ct)
    {
        HoldUntil = null;
        HoldReason = null;
        _nextHold = FirstHold;
        var now = time.GetUtcNow();
        await db.Jobs.Where(j => j.Kind == JobKinds.YoutubeDownload.Name && j.Status == JobStatus.Queued && j.NotBefore > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.NotBefore, now), ct);
        engine.Wake(JobLanes.Download);
        Wake();
    }

    /// <summary>Ajoute des vidéos à la file ; une vidéo déjà importée est ignorée, une vidéo en échec est relancée.</summary>
    public async Task<YoutubeImportResultDto> EnqueueAsync(LocalifyDbContext db, IEnumerable<YoutubeVideoDto> videos, string? albumHint, Guid? subscriptionId, CancellationToken ct)
    {
        var list = videos.Where(v => IsVideoId(v.VideoId)).DistinctBy(v => v.VideoId).ToList();
        var ids = list.Select(v => v.VideoId).ToList();
        var existing = await db.YoutubeItems.Where(i => ids.Contains(i.VideoId)).ToDictionaryAsync(i => i.VideoId, ct);
        int queued = 0, known = 0;
        foreach (var v in list)
        {
            if (existing.TryGetValue(v.VideoId, out var item))
            {
                if (item.Status is not (YoutubeItemStatus.Failed or YoutubeItemStatus.Skipped)) { known++; continue; }
                item.Status = YoutubeItemStatus.Queued;
                item.Error = null;
                item.UpdatedAt = time.GetUtcNow();
                queued++;
                continue;
            }
            db.YoutubeItems.Add(new YoutubeItem
            {
                VideoId = v.VideoId,
                Title = Truncate(v.Title, 500) ?? v.VideoId,
                Channel = Truncate(v.Channel, 200),
                DurationSeconds = v.DurationSeconds,
                ThumbnailUrl = Truncate(v.ThumbnailUrl, 1000),
                AlbumHint = Truncate(v.Album ?? albumHint, 300),
                SubscriptionId = subscriptionId,
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
            queued++;
        }
        await db.SaveChangesAsync(ct);
        if (queued > 0) Wake();
        return new YoutubeImportResultDto(queued, known);
    }

    /// <summary>Applique les métadonnées choisies par l'admin : nouveaux tags, nouvelle pochette, nouveau dossier.</summary>
    public async Task<string?> ApplyAsync(LocalifyDbContext db, Guid itemId, MetadataCandidateDto candidate, CancellationToken ct)
    {
        var item = await db.YoutubeItems.Include(i => i.Track).FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item?.Track is not { } track) return Tx.Key("errors.yt.notFound");
        if (string.IsNullOrWhiteSpace(candidate.Title) || string.IsNullOrWhiteSpace(candidate.Artist)) return Tx.Key("errors.yt.titleArtistRequired");
        if (!File.Exists(track.FilePath)) return Tx.Key("errors.yt.fileGone");

        var newPath = await writer.WriteAsync(track.FilePath, candidate, item.VideoId, item.ThumbnailUrl, ct, async target =>
        {
            // Le chemin change avant le déplacement : la surveillance du dossier ne prend pas le morceau pour un fichier supprimé
            // (il garde son identifiant, donc sa place dans les playlists et les favoris).
            track.FilePath = Path.GetFullPath(target);
            track.MetadataLocked = false;
            await db.SaveChangesAsync(ct);
        });
        await scans.ScanFilesNowAsync([newPath], ct);
        item.NeedsReview = false;
        item.MatchConfidence = 1;
        item.MatchSource = candidate.Source == "manuel" ? "manuel" : candidate.Source;
        item.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Chaque vidéo en file reçoit sa suite de tâches : téléchargement (+ conversion) → empreinte AcoustID →
    /// métadonnées, tags et rangement. Les vidéos ajoutées, relancées depuis l'admin ou rendues à la file
    /// (fichier disparu) sont prises en charge ici, sans que les appelants aient à connaître les tâches.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FeedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Import YouTube : création des tâches impossible");
            }
            await WaitAsync(_wake, TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private string WorkDir(string videoId) => Path.Combine(options.Value.YoutubeDataPath, "tmp", videoId);

    /// <summary>
    /// Imports laissés à mi-chemin sans tâche active (version précédente, suite supprimée) : ils repartent du début.
    /// Les dossiers temporaires sans import actif sont supprimés.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalifyDbContext>();
        var orphans = await db.YoutubeItems
            .Where(i => (i.Status == YoutubeItemStatus.Downloading || i.Status == YoutubeItemStatus.Downloaded || i.Status == YoutubeItemStatus.Processing)
                && !db.Jobs.Any(j => j.SubjectId == i.Id && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued || j.Status == JobStatus.Running)))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, YoutubeItemStatus.Queued), ct);
        if (orphans > 0) logger.LogInformation("{Count} import(s) YouTube interrompu(s) remis en file", orphans);

        var tmpRoot = Path.Combine(options.Value.YoutubeDataPath, "tmp");
        if (!Directory.Exists(tmpRoot)) return;
        var active = (await db.YoutubeItems.Where(i => i.Status == YoutubeItemStatus.Downloading || i.Status == YoutubeItemStatus.Downloaded || i.Status == YoutubeItemStatus.Processing)
            .Select(i => i.VideoId).ToListAsync(ct)).ToHashSet();
        foreach (var dir in Directory.EnumerateDirectories(tmpRoot).Where(d => !active.Contains(Path.GetFileName(d))))
            DeleteQuietly(dir);
    }

    private async Task FeedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalifyDbContext>();
        while (true)
        {
            var items = await db.YoutubeItems
                .Where(i => i.Status == YoutubeItemStatus.Queued
                    && !db.Jobs.Any(j => j.SubjectId == i.Id && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued || j.Status == JobStatus.Running)))
                .OrderBy(i => i.CreatedAt).Take(200).Select(i => new { i.Id, i.Title }).ToListAsync(ct);
            if (items.Count == 0) return;
            await scope.ServiceProvider.GetRequiredService<JobEngine>().EnqueueAsync(db, items.Select(i => ImportPlan(i.Id, i.Title)).ToList(), ct);
        }
    }

    internal static JobPlan ImportPlan(Guid itemId, string title) =>
        new JobPlan(JobSuiteKinds.YoutubeImport, title)
            .Then(JobKinds.YoutubeDownload, Tx.Key("jobs.steps.download"), subjectId: itemId)
            .Then(JobKinds.AcoustIdIdentify, Tx.Key("jobs.steps.acoustid"), subjectId: itemId, priority: JobKinds.YoutubeDownload.Priority)
            .Then(JobKinds.YoutubeMetadata, Tx.Key("jobs.steps.metadata"), subjectId: itemId);

    private static async Task WaitAsync(Channel<bool> wake, TimeSpan delay, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(delay);
        try
        {
            await wake.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Étape 1 : téléchargement et conversion au format choisi (plusieurs en parallèle, file « download »).</summary>
    internal async Task DownloadAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        var db = ctx.Services.GetRequiredService<LocalifyDbContext>();
        var item = await db.YoutubeItems.FirstOrDefaultAsync(i => i.Id == ctx.SubjectId, ct);
        if (item is null || item.Status is not (YoutubeItemStatus.Queued or YoutubeItemStatus.Downloading or YoutubeItemStatus.Failed))
            throw new JobAbortedException(Tx.Key("jobs.yt.notQueued"));
        if (HoldUntil is { } until && until > time.GetUtcNow()) throw new JobDeferredException(until, HoldReason ?? "");

        var id = item.Id;
        var workDir = WorkDir(item.VideoId);
        item.Attempts++;
        item.Error = null;
        await SetAsync(db, item, YoutubeItemStatus.Downloading, ct);
        _active[id] = new YoutubeProgressDto(id, item.Title, "download", 0);
        try
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            _formatOverrides.TryRemove(id, out var format);
            ctx.Info(Tx.Key("jobs.yt.downloading", ("video", item.VideoId)) + (format is null ? "" : $" ({format})"));
            await ytDlp.DownloadAsync(item.VideoId, workDir, format, settings.Settings.RetryWithoutCookies, p =>
            {
                if (_active.TryGetValue(id, out var cur)) _active[id] = cur with { Percent = p };
                ctx.Report(p / 100);
            }, ct);
            if (File.Exists(FormatsPath(item.VideoId))) File.Delete(FormatsPath(item.VideoId));
            var download = await YtDlp.ReadDownloadAsync(workDir, item.VideoId, ct) ?? throw new IOException(Tx.Key("jobs.yt.noFile"));
            var audio = await writer.PrepareAudioAsync(download.AudioPath, settings.Settings.AudioFormat, ct);
            ctx.Info(Tx.Key("jobs.yt.downloaded", ("file", Path.GetFileName(audio)), ("size", new FileInfo(audio).Length / 1024)));
            ctx.Set("audio", audio);
            await SetAsync(db, item, YoutubeItemStatus.Downloaded, ct);
            _nextHold = FirstHold;
        }
        catch (YtDlpException ex) when (ex.Kind == YtDlpFailure.Throttled)
        {
            item.Attempts--;
            item.Error = ex.Message;
            await SetAsync(db, item, YoutubeItemStatus.Queued, CancellationToken.None);
            DeleteQuietly(workDir);
            // Plusieurs téléchargements peuvent être refusés en même temps : une seule pause (et un seul doublement).
            lock (_holdLock)
            {
                if (HoldUntil is null || HoldUntil <= time.GetUtcNow())
                {
                    HoldUntil = time.GetUtcNow() + _nextHold;
                    HoldReason = ex.Message;
                    logger.LogWarning("YouTube refuse les téléchargements, pause jusqu'à {Until} : {Message}", HoldUntil, ex.Message);
                    _nextHold = TimeSpan.FromTicks(Math.Min(MaxHold.Ticks, _nextHold.Ticks * 2));
                }
            }
            throw new JobDeferredException(HoldUntil ?? time.GetUtcNow() + FirstHold, ex.Message);
        }
        catch (Exception ex) when (IsImportFailure(ex) && !ct.IsCancellationRequested)
        {
            await FailAsync(db, item, ex);
            DeleteQuietly(workDir);
            throw;
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    /// <summary>Étape 3 : métadonnées (MusicBrainz, avec l'empreinte de l'étape 2), tags, pochette, rangement, indexation.</summary>
    internal async Task ProcessAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        var db = ctx.Services.GetRequiredService<LocalifyDbContext>();
        var item = await db.YoutubeItems.FirstOrDefaultAsync(i => i.Id == ctx.SubjectId, ct);
        if (item is null || item.Status is not (YoutubeItemStatus.Downloaded or YoutubeItemStatus.Processing))
            throw new JobAbortedException(Tx.Key("jobs.yt.notDownloaded"));
        var id = item.Id;
        var workDir = WorkDir(item.VideoId);
        var download = await YtDlp.ReadDownloadAsync(workDir, item.VideoId, ct);
        if (download is null)
        {
            // Fichier disparu entre-temps : la vidéo repart au téléchargement (nouvelle suite).
            ctx.Warn(Tx.Key("jobs.yt.fileGone"));
            await SetAsync(db, item, YoutubeItemStatus.Queued, ct);
            Wake();
            return;
        }
        await SetAsync(db, item, YoutubeItemStatus.Processing, ct);
        _active[id] = new YoutubeProgressDto(id, item.Title, "metadata", null);

        try
        {
            var current = settings.Settings;
            var matches = ctx.Input["acoustid"]?.Deserialize<List<AcoustIdMatch>>(JobContext.Json) ?? [];
            var resolution = await resolver.ResolveAsync(download.Info, item.AlbumHint, matches, ct);
            ctx.Info(Tx.Key("jobs.yt.resolved", ("artist", resolution.Best.Artist), ("title", resolution.Best.Title),
                ("album", resolution.Best.Album ?? "?"), ("source", resolution.Best.Source), ("score", Math.Round(resolution.Confidence * 100))));

            _active[id] = _active[id] with { Stage = "tagging" };
            var thumbnail = YtDlp.BestThumbnail(download.Info) ?? item.ThumbnailUrl;
            var final = await writer.WriteAsync(download.AudioPath, resolution.Best, item.VideoId, thumbnail, ct);
            var track = (await scans.ScanFilesNowAsync([final], ct)).FirstOrDefault();
            ctx.Info(Tx.Key("jobs.yt.filed", ("path", Path.GetRelativePath(options.Value.UploadPath, final).Replace('\\', '/'))));

            item.Title = Truncate(YtDlp.Str(download.Info, "title"), 500) ?? item.Title;
            item.Channel = Truncate(YtDlp.Str(download.Info, "channel") ?? YtDlp.Str(download.Info, "uploader"), 200) ?? item.Channel;
            item.ThumbnailUrl = Truncate(thumbnail, 1000);
            if (download.Info["duration"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number) item.DurationSeconds = d.GetValue<double>();
            item.TrackId = track?.Id;
            item.MatchConfidence = resolution.Confidence;
            item.MatchSource = resolution.Best.Source;
            item.NeedsReview = resolution.Confidence < current.AutoAcceptThreshold;
            if (item.NeedsReview) ctx.Warn(Tx.Key("jobs.yt.needsReview"));
            item.CandidatesJson = JsonSerializer.Serialize(resolution.Candidates, Json);
            item.Error = null;
            item.CompletedAt = time.GetUtcNow();
            await SetAsync(db, item, YoutubeItemStatus.Done, ct);
            DeleteQuietly(workDir);
            logger.LogInformation("YouTube {VideoId} importé : {Artist} - {Title} ({Source}, {Confidence:P0})",
                item.VideoId, resolution.Best.Artist, resolution.Best.Title, resolution.Best.Source, resolution.Confidence);
        }
        catch (Exception ex) when (IsImportFailure(ex) && !ct.IsCancellationRequested)
        {
            await FailAsync(db, item, ex);
            DeleteQuietly(workDir);
            throw;
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    /// <summary>Suite annulée depuis l'admin : l'import passe en échec (relançable) au lieu de repartir seul.</summary>
    internal async Task CanceledAsync(IServiceProvider services, Guid? itemId, CancellationToken ct)
    {
        var db = services.GetRequiredService<LocalifyDbContext>();
        var item = await db.YoutubeItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null || item.Status is YoutubeItemStatus.Done or YoutubeItemStatus.Failed or YoutubeItemStatus.Skipped) return;
        item.Error = Tx.Key("jobs.yt.canceled");
        await SetAsync(db, item, YoutubeItemStatus.Failed, ct);
        DeleteQuietly(WorkDir(item.VideoId));
    }

    private static bool IsImportFailure(Exception ex) =>
        ex is YtDlpException or IOException or InvalidOperationException or HttpRequestException or JsonException
            or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or UnauthorizedAccessException;

    private async Task FailAsync(LocalifyDbContext db, YoutubeItem item, Exception ex)
    {
        logger.LogWarning(ex, "Import YouTube {VideoId} en échec", item.VideoId);
        item.Error = ex.Message;
        if (ex is YtDlpException { Formats: { } formats })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FormatsPath(item.VideoId))!);
            await File.WriteAllTextAsync(FormatsPath(item.VideoId), formats, CancellationToken.None);
        }
        await SetAsync(db, item, YoutubeItemStatus.Failed, CancellationToken.None);
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task SetAsync(LocalifyDbContext db, YoutubeItem item, YoutubeItemStatus status, CancellationToken ct)
    {
        item.Status = status;
        item.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    internal static bool IsVideoId(string? id) =>
        id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
