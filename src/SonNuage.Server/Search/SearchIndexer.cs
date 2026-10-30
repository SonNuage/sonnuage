using System.Threading.Channels;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Search;

/// <summary>
/// Maintient l'index Elasticsearch à jour : reconstruction complète (quelques secondes pour des dizaines de
/// milliers de titres) après chaque scan, chaque modification depuis l'admin, au retour d'Elasticsearch
/// après une panne, et toutes les 30 minutes pour rafraîchir la popularité.
/// </summary>
public sealed class SearchIndexer(
    ElasticsearchClient es,
    SearchGateway gateway,
    ScanCoordinator scans,
    IServiceScopeFactory scopes,
    ILogger<SearchIndexer> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void RequestReindex()
    {
        if (es.Configured) _requests.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!es.Configured)
        {
            logger.LogInformation("Aucun Elasticsearch configuré : recherche en base");
            return;
        }
        scans.LibraryChanged += RequestReindex;
        gateway.Recovered += RequestReindex;

        // Un index déjà rempli (redémarrage du serveur) sert immédiatement, la reconstruction suit.
        try
        {
            if (await es.CountAsync(stoppingToken) > 0) gateway.IndexReady = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Elasticsearch injoignable au démarrage : {Message}", ex.Message);
        }
        RequestReindex();

        while (!stoppingToken.IsCancellationRequested)
        {
            using (var periodic = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                periodic.CancelAfter(Periodic);
                try
                {
                    await _requests.Reader.ReadAsync(periodic.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }

            await Task.Delay(Debounce, stoppingToken);
            _requests.Reader.TryRead(out _);
            try
            {
                await ReindexAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Indexation Elasticsearch impossible, nouvel essai dans {Delay} s : {Message}", RetryAfterFailure.TotalSeconds, ex.Message);
                gateway.ReportFailure(ex);
                _ = RetryLaterAsync(stoppingToken);
            }
        }
    }

    private async Task RetryLaterAsync(CancellationToken ct)
    {
        await Task.Delay(RetryAfterFailure, ct);
        RequestReindex();
    }

    private async Task ReindexAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        List<SearchDocument> documents;
        using (var scope = scopes.CreateScope())
        {
            documents = await BuildDocumentsAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), ct);
        }
        await es.EnsureIndexAsync(ct);
        await es.ReplaceAllAsync(documents, ct);
        gateway.IndexReady = true;
        await es.DeleteOldIndexesAsync(ct);
        logger.LogInformation("Index de recherche reconstruit : {Count} documents en {Ms} ms", documents.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
    }

    internal static async Task<List<SearchDocument>> BuildDocumentsAsync(SonNuageDbContext db, CancellationToken ct)
    {
        var plays = await db.PlayEvents.GroupBy(p => p.TrackId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var tracks = await db.Tracks.AsNoTracking()
            .Select(t => new { t.Id, t.Title, t.ArtistName, t.AlbumArtistName, t.AlbumTitle, t.AlbumId, t.Genre, t.Year })
            .ToListAsync(ct);
        var albums = await db.Albums.AsNoTracking()
            .Select(a => new { a.Id, a.Title, a.Year, a.ArtistId, Artist = a.Artist.Name })
            .ToListAsync(ct);
        var artists = await db.Artists.AsNoTracking().Where(a => a.Albums.Any()).Select(a => new { a.Id, a.Name }).ToListAsync(ct);

        var albumPlays = tracks.GroupBy(t => t.AlbumId).ToDictionary(g => g.Key, g => g.Sum(t => plays.GetValueOrDefault(t.Id)));
        var artistPlays = albums.GroupBy(a => a.ArtistId).ToDictionary(g => g.Key, g => g.Sum(a => albumPlays.GetValueOrDefault(a.Id)));

        var documents = new List<SearchDocument>(tracks.Count + albums.Count + artists.Count);
        documents.AddRange(artists.Select(a => new SearchDocument(
            "artist", a.Id, a.Name, null, null, null, null, artistPlays.GetValueOrDefault(a.Id), a.Name, [a.Name])));
        documents.AddRange(albums.Select(a => new SearchDocument(
            "album", a.Id, a.Title, a.Artist, null, null, a.Year, albumPlays.GetValueOrDefault(a.Id), $"{a.Title} {a.Artist}", [a.Title])));
        documents.AddRange(tracks.Select(t => new SearchDocument(
            "track", t.Id, t.Title, t.ArtistName, t.AlbumTitle, t.Genre, t.Year, plays.GetValueOrDefault(t.Id),
            string.Join(' ', new[] { t.Title, t.ArtistName, t.AlbumArtistName == t.ArtistName ? null : t.AlbumArtistName, t.AlbumTitle, t.Genre }.Where(s => !string.IsNullOrWhiteSpace(s))),
            [t.Title])));
        return documents;
    }
}
