using SonNuage.Contracts.Localization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
    // Synchronisations demandées (nouvel abonnement, bouton « Synchroniser ») : traitées en arrière-plan, une chaîne
    // pouvant prendre plusieurs minutes à lire.
    private readonly Channel<Guid> _requests = Channel.CreateUnbounded<Guid>();

    /// <summary>Planifie la synchronisation d'un abonnement, sans l'attendre.</summary>
    public void RequestSync(Guid subscriptionId) => _requests.Writer.TryWrite(subscriptionId);

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
                if (listing.Kind != "playlist")
                {
                    sub.LastError = Tx.Key("errors.yt.isVideo");
                }
                else
                {
                    sub.Title = listing.Title.Length > 300 ? listing.Title[..300] : listing.Title;
                    var videos = listing.Entries.Select(e => new YoutubeVideoDto(e.Id, e.Title, e.Channel ?? listing.Channel, e.DurationSeconds, e.Thumbnail, null, e.Album)).ToList();
                    var albumHint = sub.IsAlbum ? listing.Title : null;
                    // Première lecture sans « importer l'existant » : ce qui est déjà publié est seulement noté, ignoré.
                    if (sub.LastSyncedAt is null && !sub.ImportExisting)
                    {
                        await SkipAsync(db, sub, videos, albumHint, ct);
                        logger.LogInformation("Abonnement « {Title} » : {Count} vidéo(s) existante(s) ignorée(s)", sub.Title, videos.Count);
                    }
                    else
                    {
                        var result = await importer.EnqueueAsync(db, videos, albumHint, sub.Id, ct);
                        logger.LogInformation("Abonnement « {Title} » : {Queued} nouvelle(s) vidéo(s)", sub.Title, result.Queued);
                    }
                    sub.LastError = null;
                }
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

    /// <summary>
    /// Nouvel abonnement, enregistré aussitôt : la lecture de la playlist ou de la chaîne (parfois des minutes) et
    /// les imports suivent en arrière-plan. Sans ImportExisting, les vidéos déjà publiées seront marquées
    /// « ignorées » à cette première lecture.
    /// </summary>
    public async Task<(YoutubeSubscription? Subscription, string? Error)> CreateAsync(SonNuageDbContext db, CreateSubscriptionRequest req, CancellationToken ct)
    {
        if (NormalizeUrl(req.Url) is not { } url) return (null, "Adresse YouTube invalide.");
        if (await db.YoutubeSubscriptions.AnyAsync(s => s.Url == url, ct)) return (null, Tx.Key("errors.yt.alreadySubscribed"));

        var sub = new YoutubeSubscription { Url = url, Title = url, IsAlbum = req.IsAlbum, ImportExisting = req.ImportExisting, CreatedAt = time.GetUtcNow() };
        db.YoutubeSubscriptions.Add(sub);
        await db.SaveChangesAsync(ct);
        RequestSync(sub.Id);
        return (sub, null);
    }

    private async Task SkipAsync(SonNuageDbContext db, YoutubeSubscription sub, List<YoutubeVideoDto> videos, string? albumHint, CancellationToken ct)
    {
        var ids = videos.Select(v => v.VideoId).ToList();
        var known = await db.YoutubeItems.Where(i => ids.Contains(i.VideoId)).Select(i => i.VideoId).ToListAsync(ct);
        foreach (var v in videos.Where(v => !known.Contains(v.VideoId) && YoutubeImporter.IsVideoId(v.VideoId)).DistinctBy(v => v.VideoId))
        {
            db.YoutubeItems.Add(new YoutubeItem
            {
                VideoId = v.VideoId, Title = v.Title.Length > 500 ? v.Title[..500] : v.Title, Channel = v.Channel, DurationSeconds = v.DurationSeconds,
                ThumbnailUrl = v.ThumbnailUrl, SubscriptionId = sub.Id, Status = YoutubeItemStatus.Skipped,
                AlbumHint = albumHint, CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(),
            });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        // Abonnements jamais lus (créés juste avant un redémarrage) : à reprendre tout de suite.
        using (var scope = scopes.CreateScope())
        {
            foreach (var id in await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().YoutubeSubscriptions
                         .Where(s => s.Enabled && s.LastSyncedAt == null).Select(s => s.Id).ToListAsync(stoppingToken))
                RequestSync(id);
        }
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
            await ServeRequestsAsync(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    /// <summary>Traite les synchronisations demandées pendant <paramref name="duration"/>, puis rend la main.</summary>
    private async Task ServeRequestsAsync(TimeSpan duration, CancellationToken stoppingToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        window.CancelAfter(duration);
        try
        {
            while (true)
            {
                var id = await _requests.Reader.ReadAsync(window.Token);
                try
                {
                    await SyncAsync(id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Synchronisation de l'abonnement {Id} impossible", id);
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
        }
    }

    [GeneratedRegex(@"^/(?:@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)/?$")]
    private static partial Regex ChannelRoot();
}
