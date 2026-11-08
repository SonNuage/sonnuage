using SonNuage.Contracts.Localization;
using System.Text.RegularExpressions;
using SonNuage.Server.Jobs;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Youtube;

/// <summary>
/// Synchronise les abonnements (playlists, albums YouTube Music, chaînes) : leurs nouvelles vidéos rejoignent
/// la file d'import. Toutes les N heures (réglable, 0 = jamais) et à la demande depuis l'admin. Chaque
/// synchronisation est une tâche du moteur (journal, annulation, historique dans le volet Tâches).
/// </summary>
public sealed partial class YoutubeSyncService(
    IServiceScopeFactory scopes,
    YtDlp ytDlp,
    YoutubeImporter importer,
    YoutubeSettingsStore settings,
    TimeProvider time,
    ILogger<YoutubeSyncService> logger) : BackgroundService
{
    /// <summary>Ce qu'a donné une synchronisation : erreur éventuelle, vidéos ajoutées à la file, vidéos ignorées.</summary>
    public sealed record SyncOutcome(string? Error, int Queued, int Skipped);

    /// <summary>
    /// Planifie la synchronisation d'un abonnement (tâche <see cref="Jobs.SubscriptionSyncJob"/>, file « sync » : une à
    /// la fois), sans l'attendre et sans doublon tant qu'elle attend son tour.
    /// </summary>
    public Task RequestSyncAsync(YoutubeSubscription sub, CancellationToken ct) =>
        scopes.RequestAsync(Jobs.MaintenanceJobs.Sync(sub.Id, sub.Title), ct);

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

    /// <summary>Lit l'abonnement et met ses nouvelles vidéos en file d'import. Exécuté par <see cref="Jobs.SubscriptionSyncJob"/>.</summary>
    public async Task<SyncOutcome> SyncAsync(Guid subscriptionId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var sub = await db.YoutubeSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (sub is null) return new SyncOutcome(Tx.Key("errors.yt.subNotFound"), 0, 0);
        int queued = 0, skipped = 0;
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
                    skipped = Skip(db, sub, videos, await KnownAsync(db, videos, ct), albumHint);
                    logger.LogInformation("Abonnement « {Title} » : {Count} vidéo(s) existante(s) ignorée(s)", sub.Title, skipped);
                }
                else
                {
                    queued = (await importer.EnqueueAsync(db, videos, albumHint, sub.Id, ct)).Queued;
                    logger.LogInformation("Abonnement « {Title} » : {Queued} nouvelle(s) vidéo(s)", sub.Title, queued);
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
        return new SyncOutcome(sub.LastError, queued, skipped);
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
        await RequestSyncAsync(sub, ct);
        return (sub, null);
    }

    private static async Task<List<string>> KnownAsync(SonNuageDbContext db, List<YoutubeVideoDto> videos, CancellationToken ct)
    {
        var ids = videos.Select(v => v.VideoId).ToList();
        return await db.YoutubeItems.Where(i => ids.Contains(i.VideoId)).Select(i => i.VideoId).ToListAsync(ct);
    }

    private int Skip(SonNuageDbContext db, YoutubeSubscription sub, List<YoutubeVideoDto> videos, List<string> known, string? albumHint)
    {
        var count = 0;
        foreach (var v in videos.Where(v => !known.Contains(v.VideoId) && YoutubeImporter.IsVideoId(v.VideoId)).DistinctBy(v => v.VideoId))
        {
            db.YoutubeItems.Add(new YoutubeItem
            {
                VideoId = v.VideoId, Title = v.Title.Length > 500 ? v.Title[..500] : v.Title, Channel = v.Channel, DurationSeconds = v.DurationSeconds,
                ThumbnailUrl = v.ThumbnailUrl, SubscriptionId = sub.Id, Status = YoutubeItemStatus.Skipped,
                AlbumHint = albumHint, CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(),
            });
            count++;
        }
        return count;
    }

    /// <summary>
    /// Toutes les 10 minutes : demande la synchronisation des abonnements dus (jamais lus, ou lus il y a plus de
    /// N heures). Les jamais lus (créés juste avant un redémarrage) repartent ainsi dès le démarrage, même sans intervalle.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var sub in await DueAsync(stoppingToken)) await RequestSyncAsync(sub, stoppingToken);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Synchronisations des abonnements non demandées : {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    private async Task<List<YoutubeSubscription>> DueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var interval = settings.Settings.SyncIntervalHours;
        var limit = time.GetUtcNow() - TimeSpan.FromHours(Math.Max(0, interval));
        var subs = await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().YoutubeSubscriptions.AsNoTracking()
            .Where(s => s.Enabled).ToListAsync(ct);
        return subs.Where(s => s.LastSyncedAt is null || (interval > 0 && s.LastSyncedAt < limit)).ToList();
    }

    [GeneratedRegex(@"^/(?:@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)/?$")]
    private static partial Regex ChannelRoot();
}
