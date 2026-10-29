using System.Linq.Expressions;
using Localify.Contracts;
using Localify.Server.Data;

namespace Localify.Server.Api;

/// <summary>Projections EF traduites en SQL : pas de chargement d'entités complètes pour les listes.</summary>
public static class Projections
{
    public static Expression<Func<Track, TrackDto>> Track(LocalifyDbContext db, Guid userId) => t => new TrackDto(
        t.Id,
        t.Title,
        t.ArtistId,
        t.ArtistName,
        t.AlbumId,
        t.Album.Title,
        t.TrackNumber,
        t.DiscNumber,
        t.DurationSeconds,
        t.Format,
        t.IsLossless,
        t.BitrateKbps,
        t.Album.CoverPath != null || t.Album.HasEmbeddedCover,
        db.Favorites.Any(f => f.UserId == userId && f.TrackId == t.Id));

    public static readonly Expression<Func<Album, AlbumDto>> Album = a => new AlbumDto(
        a.Id,
        a.Title,
        a.ArtistId,
        a.Artist.Name,
        a.Year,
        a.Tracks.Count,
        a.Tracks.Sum(t => t.DurationSeconds),
        a.CoverPath != null || a.HasEmbeddedCover);

    public static readonly Expression<Func<Artist, ArtistDto>> Artist = ar => new ArtistDto(
        ar.Id,
        ar.Name,
        ar.Albums.Count,
        ar.Albums.Sum(al => al.Tracks.Count),
        ar.Albums.Where(al => al.CoverPath != null || al.HasEmbeddedCover)
            .OrderByDescending(al => al.Year)
            .Select(al => (Guid?)al.Id)
            .FirstOrDefault());

    public static readonly Expression<Func<Playlist, PlaylistDto>> Playlist = p => new PlaylistDto(
        p.Id,
        p.Name,
        p.Description,
        p.Entries.Count,
        p.Entries.Sum(e => e.Track.DurationSeconds),
        p.Entries.OrderBy(e => e.Position)
            .Where(e => e.Track.Album.CoverPath != null || e.Track.Album.HasEmbeddedCover)
            .Select(e => (Guid?)e.Track.AlbumId)
            .FirstOrDefault(),
        p.UpdatedAt);
}
