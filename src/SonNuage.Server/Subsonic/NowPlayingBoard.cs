using System.Collections.Concurrent;

namespace SonNuage.Server.Subsonic;

/// <summary>
/// « En cours d'écoute » des applis Subsonic (scrobble avec <c>submission=false</c>), en mémoire : une entrée par
/// compte et par appli, effacée à la fin du titre (écoute complète signalée, ou durée du titre dépassée).
/// </summary>
public sealed class NowPlayingBoard(TimeProvider time)
{
    /// <summary>Marge après la durée du titre (pause, mise en mémoire tampon).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    public sealed record Entry(Guid UserId, string Username, string? Client, Guid TrackId, DateTimeOffset StartedAt, DateTimeOffset Until);

    private readonly ConcurrentDictionary<(Guid UserId, string Client), Entry> _entries = new();

    public void Start(Guid userId, string username, string? client, Guid trackId, double durationSeconds)
    {
        var now = time.GetUtcNow();
        _entries[(userId, client ?? "")] = new Entry(userId, username, client, trackId, now, now + TimeSpan.FromSeconds(Math.Max(0, durationSeconds)) + Grace);
    }

    /// <summary>Écoute terminée : le titre n'est plus « en cours » pour cette appli.</summary>
    public void Finish(Guid userId, string? client, Guid trackId)
    {
        var key = (userId, client ?? "");
        if (_entries.TryGetValue(key, out var entry) && entry.TrackId == trackId) _entries.TryRemove(KeyValuePair.Create(key, entry));
    }

    /// <summary>Les écoutes en cours, la plus récente d'abord.</summary>
    public IReadOnlyList<Entry> Current()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
            if (entry.Until <= now) _entries.TryRemove(KeyValuePair.Create(key, entry));
        return _entries.Values.Where(e => e.Until > now).OrderByDescending(e => e.StartedAt).ToList();
    }
}
