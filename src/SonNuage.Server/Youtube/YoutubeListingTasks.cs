using System.Collections.Concurrent;
using SonNuage.Contracts.Localization;

namespace SonNuage.Server.Youtube;

/// <summary>
/// Lectures de playlists et de chaînes en arrière-plan. Une grosse discographie se lit en plusieurs minutes : bien
/// plus que ce qu'une requête HTTP tient derrière un proxy (Cloudflare coupe à 100 s). L'admin lance la lecture,
/// puis interroge son avancement. Les résultats sont gardés en mémoire une demi-heure.
/// </summary>
public sealed class YoutubeListingTasks(YtDlp ytDlp, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<YoutubeListingTasks> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    /// <summary>Une même adresse relancée dans ce délai reprend la lecture en cours (double clic, rechargement).</summary>
    private static readonly TimeSpan Reuse = TimeSpan.FromMinutes(2);

    public sealed record Entry(Guid Id, string Url, int Limit, DateTimeOffset StartedAt, ListProgress Progress, Task<YtListing> Listing);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Entry Start(string url, int limit)
    {
        var now = time.GetUtcNow();
        foreach (var old in _entries.Values.Where(e => now - e.StartedAt > Retention)) _entries.TryRemove(old.Id, out _);
        if (_entries.Values.FirstOrDefault(e => e.Url == url && e.Limit == limit && now - e.StartedAt < Reuse && !e.Listing.IsFaulted) is { } running)
            return running;

        var progress = new ListProgress();
        var entry = new Entry(Guid.NewGuid(), url, limit, now, progress, Task.Run(() => ListAsync(url, limit, progress)));
        _entries[entry.Id] = entry;
        return entry;
    }

    public Entry? Get(Guid id) => _entries.GetValueOrDefault(id);

    private async Task<YtListing> ListAsync(string url, int limit, ListProgress progress)
    {
        try
        {
            return await ytDlp.ListAsync(url, limit, lifetime.ApplicationStopping, progress);
        }
        catch (Exception ex) when (ex is not (YtDlpException or OperationCanceledException))
        {
            logger.LogWarning(ex, "Lecture de {Url} impossible", url);
            throw new YtDlpException(YtDlpFailure.Other, Tx.Key("errors.yt.unreachable", ("message", ex.Message)));
        }
    }
}
