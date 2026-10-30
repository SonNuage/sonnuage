using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Search;

/// <summary>
/// Point d'entrée de la recherche : Elasticsearch quand il est configuré et répond à temps,
/// sinon recherche « simple » en base (sous-chaînes normalisées, sans tolérance aux fautes).
/// </summary>
public sealed class SearchService(SonNuageDbContext db, SearchGateway gateway, ElasticsearchClient es)
{
    public async Task<SearchResultDto> SearchAsync(string query, int limit, Guid userId, CancellationToken ct)
    {
        var whole = NameNormalizer.Key(query);
        if (whole.Length == 0) return new SearchResultDto([], [], []);

        var hits = await gateway.TryAsync($"search|{limit}|{whole}", c => es.SearchAsync(query.Trim(), limit, c))
            ?? await DatabaseSearchAsync(query, limit, ct);

        var artists = await InOrderAsync(db.Artists.Where(a => hits.Artists.Contains(a.Id)).Select(Projections.Artist), a => a.Id, hits.Artists, ct);
        var albums = await InOrderAsync(db.Albums.Where(a => hits.Albums.Contains(a.Id)).Select(Projections.Album), a => a.Id, hits.Albums, ct);
        var tracks = await LibraryEndpoints.TracksInOrderAsync(db, userId, hits.Tracks.ToList(), ct);
        return new SearchResultDto(artists, albums, tracks, hits.Correction);
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit, CancellationToken ct)
    {
        var key = NameNormalizer.Key(prefix);
        if (key.Length == 0) return [];
        return await gateway.TryAsync($"suggest|{limit}|{key}", c => es.SuggestAsync(prefix.Trim(), limit, c))
            ?? await DatabaseSuggestAsync(key, limit, ct);
    }

    private async Task<SearchHits> DatabaseSearchAsync(string query, int limit, CancellationToken ct)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NameNormalizer.Key).Where(k => k.Length > 0).ToList();
        var whole = NameNormalizer.Key(query);

        var artists = await db.Artists.Where(a => a.Albums.Any() && a.NormalizedName.Contains(whole))
            .OrderBy(a => a.NormalizedName.Length).Take(limit).Select(a => a.Id).ToListAsync(ct);
        var albums = await db.Albums.Where(a => a.NormalizedTitle.Contains(whole) || a.Artist.NormalizedName.Contains(whole))
            .OrderBy(a => a.NormalizedTitle.Length).Take(limit).Select(a => a.Id).ToListAsync(ct);
        var trackQuery = db.Tracks.AsQueryable();
        foreach (var token in tokens) trackQuery = trackQuery.Where(t => t.SearchKey.Contains(token));
        var tracks = await trackQuery.OrderBy(t => t.Title.Length).Take(limit).Select(t => t.Id).ToListAsync(ct);
        return new SearchHits(artists, albums, tracks, null);
    }

    private async Task<IReadOnlyList<string>> DatabaseSuggestAsync(string key, int limit, CancellationToken ct)
    {
        var names = await db.Artists.Where(a => a.Albums.Any() && a.NormalizedName.StartsWith(key)).OrderBy(a => a.NormalizedName).Select(a => a.Name).Take(limit).ToListAsync(ct);
        names.AddRange(await db.Albums.Where(a => a.NormalizedTitle.StartsWith(key)).OrderBy(a => a.NormalizedTitle).Select(a => a.Title).Take(limit).ToListAsync(ct));
        names.AddRange(await db.Tracks.Where(t => t.SearchKey.StartsWith(key)).OrderBy(t => t.Title).Select(t => t.Title).Take(limit).ToListAsync(ct));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }

    private static async Task<List<T>> InOrderAsync<T>(IQueryable<T> query, Func<T, Guid> id, IReadOnlyList<Guid> order, CancellationToken ct)
    {
        if (order.Count == 0) return [];
        var byId = (await query.ToListAsync(ct)).ToDictionary(id);
        return order.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }
}
