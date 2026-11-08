using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

/// <summary>
/// Découverte dans la bibliothèque, sans donnée externe (couche 1 de la feuille de route) :
/// « À réécouter » et « Pas encore écouté », propres à chaque utilisateur.
/// </summary>
internal static class HomeDiscovery
{
    /// <summary>Un titre devient « à réécouter » s'il n'a pas été joué depuis ce nombre de jours.</summary>
    public const int RediscoverAfterDays = 30;

    /// <summary>Écoutes minimales pour qu'un titre non liké compte parmi les habitudes.</summary>
    public const int MinPlays = 2;

    /// <summary>Titres souvent écoutés, ou likés il y a longtemps, mais pas joués ces derniers temps.</summary>
    public static async Task<List<Guid>> RediscoverTrackIdsAsync(SonNuageDbContext db, Guid userId, DateTimeOffset now, int count, CancellationToken ct)
    {
        var cutoff = now.AddDays(-RediscoverAfterDays);
        var playedLately = db.PlayEvents.Where(p => p.UserId == userId && p.PlayedAt >= cutoff).Select(p => p.TrackId);

        var habits = await db.PlayEvents
            .Where(p => p.UserId == userId && !playedLately.Contains(p.TrackId))
            .GroupBy(p => p.TrackId)
            .Select(g => new { TrackId = g.Key, Plays = g.Count() })
            .Where(x => x.Plays >= MinPlays)
            .OrderByDescending(x => x.Plays)
            .Take(count)
            .Select(x => x.TrackId)
            .ToListAsync(ct);
        var liked = await db.Favorites
            .Where(f => f.UserId == userId && f.CreatedAt < cutoff && !playedLately.Contains(f.TrackId))
            .OrderByDescending(f => f.CreatedAt)
            .Take(count)
            .Select(f => f.TrackId)
            .ToListAsync(ct);

        return habits.Concat(liked).Distinct().Take(count).ToList();
    }

    /// <summary>
    /// Albums dont l'utilisateur n'a encore lancé aucun titre, les plus récents d'abord. Vide tant qu'il n'a rien
    /// écouté : la rubrique répéterait alors « Ajouts récents ».
    /// </summary>
    public static async Task<List<Guid>> NotYetPlayedAlbumIdsAsync(SonNuageDbContext db, Guid userId, int count, CancellationToken ct)
    {
        var plays = db.PlayEvents.Where(p => p.UserId == userId);
        if (!await plays.AnyAsync(ct)) return [];
        var playedAlbums = plays.Select(p => p.Track.AlbumId);
        return await db.Albums
            .Where(a => !playedAlbums.Contains(a.Id))
            .OrderByDescending(a => a.CreatedAt)
            .Take(count)
            .Select(a => a.Id)
            .ToListAsync(ct);
    }
}
