using Localify.Contracts.Localization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Localify.Contracts;
using Localify.Server.Data;
using Localify.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Youtube;

/// <summary>
/// File d'import YouTube, traitée une vidéo à la fois (YouTube bride vite les téléchargements parallèles) :
/// téléchargement → conversion → métadonnées → tags et pochette → rangement → indexation immédiate.
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
    private TimeSpan _nextHold = FirstHold;

    public DateTimeOffset? HoldUntil { get; private set; }
    public string? HoldReason { get; private set; }
    public YoutubeProgressDto? Current { get; private set; }

    public void Wake() => _wake.Writer.TryWrite(true);

    // Format yt-dlp choisi par l'admin pour la prochaine tentative d'un import (une seule fois, non conservé).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _formatOverrides = new();

    public void UseFormatOnce(Guid itemId, string format) => _formatOverrides[itemId] = format;

    /// <summary>Formats proposés par YouTube, relevés lors du dernier échec « format indisponible ».</summary>
    public string FormatsPath(string videoId) => Path.Combine(options.Value.YoutubeDataPath, "formats", $"{videoId}.txt");

    public void Resume()
    {
        HoldUntil = null;
        HoldReason = null;
        _nextHold = FirstHold;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Un redémarrage pendant un import : on reprend ces vidéos depuis le début.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LocalifyDbContext>();
            await db.YoutubeItems.Where(i => i.Status == YoutubeItemStatus.Downloading || i.Status == YoutubeItemStatus.Processing)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, YoutubeItemStatus.Queued), stoppingToken);
        }
        var tmpRoot = Path.Combine(options.Value.YoutubeDataPath, "tmp");
        if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (HoldUntil is { } until && until > time.GetUtcNow())
            {
                await WaitAsync(until - time.GetUtcNow(), stoppingToken);
                continue;
            }
            HoldUntil = null;

            Guid? next;
            using (var scope = scopes.CreateScope())
            {
                next = await scope.ServiceProvider.GetRequiredService<LocalifyDbContext>().YoutubeItems
                    .Where(i => i.Status == YoutubeItemStatus.Queued)
                    .OrderBy(i => i.CreatedAt).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(stoppingToken);
            }
            if (next is null)
            {
                await WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            try
            {
                await ProcessAsync(next.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                Current = null;
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (delay != Timeout.InfiniteTimeSpan) cts.CancelAfter(delay);
        try
        {
            await _wake.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalifyDbContext>();
        var item = await db.YoutubeItems.FirstAsync(i => i.Id == id, ct);
        var workDir = Path.Combine(options.Value.YoutubeDataPath, "tmp", item.VideoId);
        await SetAsync(db, item, YoutubeItemStatus.Downloading, ct);
        item.Attempts++;
        Current = new YoutubeProgressDto(item.Id, item.Title, "download", 0);

        try
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            _formatOverrides.TryRemove(item.Id, out var format);
            var download = await ytDlp.DownloadAsync(item.VideoId, workDir, format, settings.Settings.RetryWithoutCookies,
                p => Current = Current! with { Percent = p }, ct);
            if (File.Exists(FormatsPath(item.VideoId))) File.Delete(FormatsPath(item.VideoId));

            await SetAsync(db, item, YoutubeItemStatus.Processing, ct);
            Current = Current! with { Stage = "metadata", Percent = null };
            var current = settings.Settings;
            var audio = await writer.PrepareAudioAsync(download.AudioPath, current.AudioFormat, ct);
            var resolution = await resolver.ResolveAsync(download.Info, audio, item.AlbumHint, current.AcoustIdKey, ct);

            Current = Current with { Stage = "tagging" };
            var thumbnail = YtDlp.BestThumbnail(download.Info) ?? item.ThumbnailUrl;
            var final = await writer.WriteAsync(audio, resolution.Best, item.VideoId, thumbnail, ct);
            var track = (await scans.ScanFilesNowAsync([final], ct)).FirstOrDefault();

            item.Title = Truncate(YtDlp.Str(download.Info, "title"), 500) ?? item.Title;
            item.Channel = Truncate(YtDlp.Str(download.Info, "channel") ?? YtDlp.Str(download.Info, "uploader"), 200) ?? item.Channel;
            item.ThumbnailUrl = Truncate(thumbnail, 1000);
            if (download.Info["duration"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number) item.DurationSeconds = d.GetValue<double>();
            item.TrackId = track?.Id;
            item.MatchConfidence = resolution.Confidence;
            item.MatchSource = resolution.Best.Source;
            item.NeedsReview = resolution.Confidence < current.AutoAcceptThreshold;
            item.CandidatesJson = JsonSerializer.Serialize(resolution.Candidates, Json);
            item.Error = null;
            item.CompletedAt = time.GetUtcNow();
            await SetAsync(db, item, YoutubeItemStatus.Done, ct);
            _nextHold = FirstHold;
            logger.LogInformation("YouTube {VideoId} importé : {Artist} - {Title} ({Source}, {Confidence:P0})",
                item.VideoId, resolution.Best.Artist, resolution.Best.Title, resolution.Best.Source, resolution.Confidence);
        }
        catch (YtDlpException ex) when (ex.Kind == YtDlpFailure.Throttled)
        {
            item.Attempts--;
            item.Error = ex.Message;
            await SetAsync(db, item, YoutubeItemStatus.Queued, CancellationToken.None);
            HoldUntil = time.GetUtcNow() + _nextHold;
            HoldReason = ex.Message;
            logger.LogWarning("YouTube refuse les téléchargements, pause jusqu'à {Until} : {Message}", HoldUntil, ex.Message);
            _nextHold = TimeSpan.FromTicks(Math.Min(MaxHold.Ticks, _nextHold.Ticks * 2));
        }
        catch (Exception ex) when (ex is YtDlpException or IOException or InvalidOperationException or HttpRequestException or JsonException
                                       or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or UnauthorizedAccessException
                                   && !ct.IsCancellationRequested)
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
        finally
        {
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
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
