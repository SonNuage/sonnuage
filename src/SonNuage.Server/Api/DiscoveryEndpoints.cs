using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

/// <summary>Titres phares d'un artiste et lecture infinie (morceaux proches de ce qu'on écoute).</summary>
public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization().WithTags("Library");

        // Les plus écoutés (tous comptes confondus), puis dans l'ordre de la discographie.
        g.MapGet("/artists/{id:guid}/top", async (Guid id, int? limit, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
            await db.Tracks.Where(t => t.Album.ArtistId == id || t.ArtistId == id)
                .OrderByDescending(t => db.PlayEvents.Count(p => p.TrackId == t.Id))
                .ThenBy(t => t.Album.Year).ThenBy(t => t.Album.Title).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                .Take(Math.Clamp(limit ?? 10, 1, 50))
                .Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct));

        g.MapPost("/radio", async (RadioRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (req.Count is < 1 or > RadioRequest.MaxCount || req.SeedTrackIds.Count > RadioRequest.MaxIds || req.ExcludeTrackIds.Count > RadioRequest.MaxIds)
                return Results.BadRequest();
            var ids = await NextAsync(db, req, ct);
            var tracks = await db.Tracks.Where(t => ids.Contains(t.Id)).Select(Projections.Track(db, http.User.UserId())).ToListAsync(ct);
            return Results.Ok(ids.Select(id => tracks.First(t => t.Id == id)).ToList());
        });
    }

    /// <summary>
    /// Morceaux proches, du plus au moins proche : même genre qu'un des morceaux de départ, puis même artiste, puis
    /// n'importe lequel. Au hasard dans chaque cercle, jamais un morceau exclu ni deux fois le même.
    /// </summary>
    private static async Task<List<Guid>> NextAsync(SonNuageDbContext db, RadioRequest req, CancellationToken ct)
    {
        var seeds = await db.Tracks.Where(t => req.SeedTrackIds.Contains(t.Id))
            .Select(t => new { Genre = t.Genre == null ? null : t.Genre.ToLower(), t.ArtistId, AlbumArtistId = t.Album.ArtistId }).ToListAsync(ct);
        var genres = seeds.Where(s => !string.IsNullOrWhiteSpace(s.Genre)).Select(s => s.Genre!).Distinct().ToList();
        var artists = seeds.SelectMany(s => new[] { s.ArtistId, s.AlbumArtistId }).Distinct().ToList();

        var picked = new List<Guid>();
        async Task FillAsync(IQueryable<Track> circle)
        {
            if (picked.Count >= req.Count) return;
            picked.AddRange(await circle.Where(t => !req.ExcludeTrackIds.Contains(t.Id) && !picked.Contains(t.Id))
                .OrderBy(_ => EF.Functions.Random()).Take(req.Count - picked.Count).Select(t => t.Id).ToListAsync(ct));
        }

        if (genres.Count > 0) await FillAsync(db.Tracks.Where(t => t.Genre != null && genres.Contains(t.Genre.ToLower())));
        if (artists.Count > 0) await FillAsync(db.Tracks.Where(t => artists.Contains(t.ArtistId) || artists.Contains(t.Album.ArtistId)));
        await FillAsync(db.Tracks);
        return picked;
    }
}
