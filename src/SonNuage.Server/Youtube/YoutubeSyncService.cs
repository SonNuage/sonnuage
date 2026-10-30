using SonNuage.Contracts.Localization;
using System.Text.RegularExpressions;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Youtube;

/// <summary>
/// Synchronise les abonnements (playlists, albums YouTube Music, chaînes) : leurs nouvelles vidéos rejoignent
/// la file d'import. Toutes les N heures (réglable, 0 = jamais) et à la demande depuis l'admin.
/// </summary>
public sealed partial class YoutubeSyncService(
    IServiceScopeFactory scopes,
    YtDlp ytDlp,
    YoutubeImporter importer,
    YoutubeSettingsStore settings,
    TimeProvider time,
    ILogger<YoutubeSyncService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// N'accepte que des adresses YouTube (yt-dlp sait lire des milliers de sites : pas question de lui faire
    /// visiter une adresse arbitraire) et normalise les chaînes vers leur onglet « Vidéos ».
    /// </summary>
    public static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be")) return null;
        var url = new UriBuilder(uri) { Scheme = "https", Port = -1, Fragment = "" }.Uri.ToString();
        // Une chaîne reste à sa racine : yt-dlp en lit les onglets (Vidéos, ou « Albums et singles » à défaut).
        return ChannelRoot().IsMatch(uri.AbsolutePath) ? url.TrimEnd('/') : url;
    }

    /// <summary>Adresse de la racine d'une chaîne (sans onglet) : « …/@orelsan », « …/channel/UC… ».</summary>
    public static bool IsChannelRoot(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && ChannelRoot().IsMatch(uri.AbsolutePath);

    public async Task<string?> SyncAsync(Guid subscriptionId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var sub = await db.YoutubeSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
            if (sub is null) return Tx.Key("errors.yt.subNotFound");
            try
            {
                var listing = await ytDlp.ListAsync(sub.Url, null, ct);
                sub.Title = listing.Title.Length > 300 ? listing.Title[..300] : listing.Title;
                var videos = listing.Entries.Select(e => new YoutubeVideoDto(e.Id, e.Title, e.Channel ?? listing.Channel, e.DurationSeconds, e.Thumbnail, null, e.Album));
                var result = await importer.EnqueueAsync(db, videos, sub.IsAlbum ? listing.Title : null, sub.Id, ct);
                sub.LastError = null;
                logger.LogInformation("Abonnement « {Title} » : {Queued} nouvelle(s) vidéo(s)", sub.Title, result.Queued);
            }
            catch (YtDlpException ex)
            {
                sub.LastError = ex.Message;
            }
            sub.LastSyncedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return sub.LastError;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Nouvel abonnement. Sans <paramref name="importExisting"/>, les vidéos déjà publiées sont marquées « ignorées ».</summary>
    public async Task<(YoutubeSubscription? Subscription, string? Error)> CreateAsync(SonNuageDbContext db, CreateSubscriptionRequest req, CancellationToken ct)
    {
        if (NormalizeUrl(req.Url) is not { } url) return (null, "Adresse YouTube invalide.");
        if (await db.YoutubeSubscriptions.AnyAsync(s => s.Url == url, ct)) return (null, Tx.Key("errors.yt.alreadySubscribed"));
        YtListing listing;
        try
        {
            listing = await ytDlp.ListAsync(url, null, ct);
        }
        catch (YtDlpException ex)
        {
            return (null, ex.Message);
        }
        if (listing.Kind != "playlist") return (null, Tx.Key("errors.yt.isVideo"));

        var sub = new YoutubeSubscription { Url = url, Title = listing.Title.Length > 300 ? listing.Title[..300] : listing.Title, IsAlbum = req.IsAlbum, CreatedAt = time.GetUtcNow() };
        db.YoutubeSubscriptions.Add(sub);
        await db.SaveChangesAsync(ct);
        var videos = listing.Entries.Select(e => new YoutubeVideoDto(e.Id, e.Title, e.Channel ?? listing.Channel, e.DurationSeconds, e.Thumbnail, null, e.Album)).ToList();
        if (req.ImportExisting)
        {
            await importer.EnqueueAsync(db, videos, req.IsAlbum ? listing.Title : null, sub.Id, ct);
        }
        else
        {
            var ids = videos.Select(v => v.VideoId).ToList();
            var known = await db.YoutubeItems.Where(i => ids.Contains(i.VideoId)).Select(i => i.VideoId).ToListAsync(ct);
            foreach (var v in videos.Where(v => !known.Contains(v.VideoId) && YoutubeImporter.IsVideoId(v.VideoId)).DistinctBy(v => v.VideoId))
            {
                db.YoutubeItems.Add(new YoutubeItem
                {
                    VideoId = v.VideoId, Title = v.Title.Length > 500 ? v.Title[..500] : v.Title, Channel = v.Channel, DurationSeconds = v.DurationSeconds,
                    ThumbnailUrl = v.ThumbnailUrl, SubscriptionId = sub.Id, Status = YoutubeItemStatus.Skipped,
                    AlbumHint = req.IsAlbum ? listing.Title : null, CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(),
                });
            }
        }
        sub.LastSyncedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return (sub, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = settings.Settings.SyncIntervalHours;
            if (interval > 0)
            {
                List<Guid> due;
                using (var scope = scopes.CreateScope())
                {
                    var limit = time.GetUtcNow() - TimeSpan.FromHours(interval);
                    due = (await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().YoutubeSubscriptions
                            .Where(s => s.Enabled).Select(s => new { s.Id, s.LastSyncedAt }).ToListAsync(stoppingToken))
                        .Where(s => s.LastSyncedAt is null || s.LastSyncedAt < limit).Select(s => s.Id).ToList();
                }
                foreach (var id in due)
                {
                    // Pas de synchronisation pendant que YouTube nous bride : elle échouerait aussi.
                    if (importer.HoldUntil > time.GetUtcNow()) break;
                    await SyncAsync(id, stoppingToken);
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    [GeneratedRegex(@"^/(?:@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)/?$")]
    private static partial Regex ChannelRoot();
}
