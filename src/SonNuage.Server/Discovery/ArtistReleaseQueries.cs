using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Discovery;

/// <summary>Ce qu'on montre des parutions lues (<see cref="ArtistRelease"/>) : filtrées par <see cref="ReleaseRules"/>, rattachées à la bibliothèque.</summary>
public static class ArtistReleaseQueries
{
    /// <summary>Ce qu'on garde de la bibliothèque pour une parution : à quel artiste elle va, et si elle y est déjà.</summary>
    public enum Presence
    {
        Any,
        InLibrary,
        Missing,
    }

    /// <summary>
    /// Sorties récentes (90 derniers jours) et à venir, des plus récentes aux plus anciennes, puis celles à venir, de la
    /// plus proche à la plus lointaine.
    /// </summary>
    public static async Task<List<ArtistReleaseDto>> NewAsync(SonNuageDbContext db, DateOnly today, ReleaseWindow? when, ReleaseKind? kind,
        Presence presence, int limit, CancellationToken ct)
    {
        var from = today.AddDays(-ReleaseRules.RecentDays);
        var query = db.ArtistReleases.AsNoTracking().Where(r => r.ReleaseDate != null && r.ReleaseDate >= from);
        if (when == ReleaseWindow.Recent) query = query.Where(r => r.ReleaseDate <= today);
        if (when == ReleaseWindow.Upcoming) query = query.Where(r => r.ReleaseDate > today);
        var dtos = await ToDtosAsync(db, await query.ToListAsync(ct), today, kind, presence, ct);
        return dtos.OrderBy(d => d.Upcoming).ThenBy(d => d.Upcoming ? d.Date : null).ThenByDescending(d => d.Upcoming ? null : d.Date)
            .Take(limit).ToList();
    }

    /// <summary>Parutions d'un artiste de la bibliothèque absentes de celle-ci, des plus récentes (ou à venir) aux plus anciennes.</summary>
    public static async Task<List<ArtistReleaseDto>> MissingForArtistAsync(SonNuageDbContext db, Guid artistId, DateOnly today, CancellationToken ct)
    {
        var mbid = await db.Artists.Where(a => a.Id == artistId).Select(a => a.MusicBrainzId).FirstOrDefaultAsync(ct);
        if (mbid is null) return [];
        var rows = await db.ArtistReleases.AsNoTracking().Where(r => r.ArtistMbid == mbid).ToListAsync(ct);
        var dtos = await ToDtosAsync(db, rows, today, null, Presence.Missing, ct);
        return dtos.OrderByDescending(d => d.Date ?? "").ThenBy(d => d.Title).ToList();
    }

    private static async Task<List<ArtistReleaseDto>> ToDtosAsync(SonNuageDbContext db, List<ArtistRelease> rows, DateOnly today, ReleaseKind? kind,
        Presence presence, CancellationToken ct)
    {
        var mbids = rows.Select(r => r.ArtistMbid).Distinct().ToList();
        var artists = (await db.Artists.AsNoTracking().Where(a => a.MusicBrainzId != null && mbids.Contains(a.MusicBrainzId) && a.Albums.Any())
                .Select(a => new
                {
                    a.Id,
                    a.Name,
                    Mbid = a.MusicBrainzId!,
                    Albums = a.Albums.Select(al => new { al.Id, al.NormalizedTitle, al.MusicBrainzReleaseGroupId }).ToList(),
                })
                .ToListAsync(ct))
            // Deux artistes avec le même identifiant : celui qui a le plus d'albums.
            .GroupBy(a => a.Mbid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Albums.Count).First());

        var result = new List<ArtistReleaseDto>();
        foreach (var row in rows.DistinctBy(r => r.ReleaseGroupId))
        {
            if (!artists.TryGetValue(row.ArtistMbid, out var artist)) continue;
            if (ReleaseRules.KindOf(row.PrimaryType, row.SecondaryTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)) is not { } rowKind) continue;
            if (kind is not null && rowKind != kind) continue;
            var albumId = ReleaseRules.MatchAlbum(row.ReleaseGroupId, row.Title,
                artist.Albums.Select(al => new LibraryAlbumKey(al.Id, al.NormalizedTitle, al.MusicBrainzReleaseGroupId)));
            if (presence == Presence.InLibrary && albumId is null || presence == Presence.Missing && albumId is not null) continue;
            result.Add(new ArtistReleaseDto(row.ReleaseGroupId, row.Title, artist.Id, artist.Name, rowKind.ToString().ToLowerInvariant(),
                row.FirstReleaseDate ?? row.ReleaseDate?.ToString("yyyy-MM-dd"), ReleaseRules.WindowOf(row.ReleaseDate, today) == ReleaseWindow.Upcoming, albumId));
        }
        return result;
    }
}
