using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using SonNuage.Server.Data;
using SonNuage.Server.Library;

namespace SonNuage.Server.Subsonic;

/// <summary>Morceau, album, artiste, playlist tels que les décrit Subsonic, lus en une requête SQL chacun.</summary>
public static class SubsonicLibrary
{
    public const string IgnoredArticles = "The Les Le La L'";

    public sealed record SongRow(Guid Id, string Title, string ArtistName, Guid ArtistId, string AlbumArtistName, string AlbumTitle,
        Guid AlbumId, int? TrackNumber, int? DiscNumber, int? Year, string? Genre, double Duration, int? Bitrate, int? SampleRate,
        long Size, string FilePath, bool HasCover, DateTimeOffset Created, DateTimeOffset? Starred, Guid? MusicBrainzId, int PlayCount);

    public sealed record AlbumRow(Guid Id, string Title, Guid ArtistId, string ArtistName, int? Year, int SongCount, double Duration,
        bool HasCover, DateTimeOffset Created, string? Genre, Guid? MusicBrainzId, int PlayCount);

    public sealed record ArtistRow(Guid Id, string Name, string SortName, int AlbumCount, Guid? CoverAlbumId, string? MusicBrainzId);

    public sealed record PlaylistRow(Guid Id, string Name, string? Comment, string Owner, int SongCount, double Duration,
        DateTimeOffset Created, DateTimeOffset Changed, Guid? CoverAlbumId);

    public static Expression<Func<Track, SongRow>> Song(SonNuageDbContext db, Guid userId) => t => new SongRow(
        t.Id, t.Title, t.ArtistName, t.ArtistId, t.AlbumArtistName, t.Album.Title, t.AlbumId, t.TrackNumber, t.DiscNumber,
        t.Year, t.Genre, t.DurationSeconds, t.BitrateKbps, t.SampleRate, t.FileSize, t.FilePath,
        t.Album.CoverPath != null || t.Album.HasEmbeddedCover, t.CreatedAt,
        db.Favorites.Where(f => f.UserId == userId && f.TrackId == t.Id).Select(f => (DateTimeOffset?)f.CreatedAt).FirstOrDefault(),
        t.MusicBrainzRecordingId,
        db.PlayEvents.Count(p => p.UserId == userId && p.TrackId == t.Id));

    public static Expression<Func<Album, AlbumRow>> Album(SonNuageDbContext db, Guid userId) => a => new AlbumRow(
        a.Id, a.Title, a.ArtistId, a.Artist.Name, a.Year, a.Tracks.Count, a.Tracks.Sum(t => t.DurationSeconds),
        a.CoverPath != null || a.HasEmbeddedCover, a.CreatedAt,
        a.Tracks.Where(t => t.Genre != null).Select(t => t.Genre).FirstOrDefault(),
        a.MusicBrainzReleaseId,
        db.PlayEvents.Count(p => p.UserId == userId && p.Track.AlbumId == a.Id));

    public static readonly Expression<Func<Artist, ArtistRow>> ArtistRows = ar => new ArtistRow(
        ar.Id, ar.Name, ar.SortName, ar.Albums.Count,
        ar.Albums.Where(al => al.CoverPath != null || al.HasEmbeddedCover).OrderByDescending(al => al.Year).Select(al => (Guid?)al.Id).FirstOrDefault(),
        ar.MusicBrainzId);

    public static readonly Expression<Func<Playlist, PlaylistRow>> PlaylistRows = p => new PlaylistRow(
        p.Id, p.Name, p.Description, p.Owner.Username, p.Entries.Count, p.Entries.Sum(e => e.Track.DurationSeconds),
        p.CreatedAt, p.UpdatedAt,
        p.Entries.OrderBy(e => e.Position).Where(e => e.Track.Album.CoverPath != null || e.Track.Album.HasEmbeddedCover)
            .Select(e => (Guid?)e.Track.AlbumId).FirstOrDefault());

    // ---- Éléments de réponse ----

    public static SubsonicNode Fill(this SubsonicNode n, SongRow s)
    {
        var suffix = Path.GetExtension(s.FilePath).TrimStart('.').ToLowerInvariant();
        return n.Attr("id", SubsonicIds.Of(SubsonicIds.Track, s.Id))
            .Attr("parent", SubsonicIds.Of(SubsonicIds.Album, s.AlbumId))
            .Attr("isDir", false)
            .Attr("title", s.Title)
            .Attr("album", s.AlbumTitle)
            .Attr("artist", s.ArtistName)
            .Attr("track", s.TrackNumber)
            .Attr("discNumber", s.DiscNumber)
            .Attr("year", s.Year)
            .Attr("genre", s.Genre)
            .Attr("coverArt", s.HasCover ? SubsonicIds.Of(SubsonicIds.Album, s.AlbumId) : null)
            .Attr("size", s.Size)
            .Attr("contentType", LibraryFiles.ContentType(s.FilePath))
            .Attr("suffix", suffix)
            .Attr("duration", (int)Math.Round(s.Duration))
            .Attr("bitRate", s.Bitrate)
            .Attr("samplingRate", s.SampleRate)
            .Attr("path", $"{s.AlbumArtistName}/{s.AlbumTitle}/{Path.GetFileName(s.FilePath)}")
            .Attr("isVideo", false)
            .Attr("playCount", s.PlayCount)
            .Attr("created", s.Created)
            .Attr("starred", s.Starred)
            .Attr("albumId", SubsonicIds.Of(SubsonicIds.Album, s.AlbumId))
            .Attr("artistId", SubsonicIds.Of(SubsonicIds.Artist, s.ArtistId))
            .Attr("type", "music")
            .Attr("mediaType", "song")
            .Attr("musicBrainzId", s.MusicBrainzId?.ToString());
    }

    /// <param name="legacy">Album vu comme un dossier (getAlbumList, getMusicDirectory, search2) : attributs de l'ancien format en plus.</param>
    public static SubsonicNode Fill(this SubsonicNode n, AlbumRow a, bool legacy = false)
    {
        var id = SubsonicIds.Of(SubsonicIds.Album, a.Id);
        n.Attr("id", id);
        if (legacy) n.Attr("parent", SubsonicIds.Of(SubsonicIds.Artist, a.ArtistId)).Attr("isDir", true).Attr("title", a.Title).Attr("album", a.Title);
        return n.Attr("name", a.Title)
            .Attr("artist", a.ArtistName)
            .Attr("artistId", SubsonicIds.Of(SubsonicIds.Artist, a.ArtistId))
            .Attr("coverArt", a.HasCover ? id : null)
            .Attr("songCount", a.SongCount)
            .Attr("duration", (int)Math.Round(a.Duration))
            .Attr("playCount", a.PlayCount)
            .Attr("created", a.Created)
            .Attr("year", a.Year)
            .Attr("genre", a.Genre)
            .Attr("musicBrainzId", a.MusicBrainzId?.ToString());
    }

    public static SubsonicNode Fill(this SubsonicNode n, ArtistRow a) => n
        .Attr("id", SubsonicIds.Of(SubsonicIds.Artist, a.Id))
        .Attr("name", a.Name)
        .Attr("albumCount", a.AlbumCount)
        .Attr("coverArt", a.CoverAlbumId is null ? null : SubsonicIds.Of(SubsonicIds.Artist, a.Id))
        .Attr("musicBrainzId", a.MusicBrainzId);

    public static SubsonicNode Fill(this SubsonicNode n, PlaylistRow p) => n
        .Attr("id", SubsonicIds.Of(SubsonicIds.Playlist, p.Id))
        .Attr("name", p.Name)
        .Attr("comment", p.Comment)
        .Attr("owner", p.Owner)
        .Attr("public", false)
        .Attr("songCount", p.SongCount)
        .Attr("duration", (int)Math.Round(p.Duration))
        .Attr("created", p.Created)
        .Attr("changed", p.Changed)
        .Attr("coverArt", p.CoverAlbumId is null ? null : SubsonicIds.Of(SubsonicIds.Playlist, p.Id));

    /// <summary>Lettre d'index d'un artiste (sans article ni accent), « # » pour le reste.</summary>
    public static string IndexLetter(string sortName)
    {
        var first = sortName.Trim().Normalize(NormalizationForm.FormD).FirstOrDefault(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        var upper = char.ToUpperInvariant(first);
        return upper is >= 'A' and <= 'Z' ? upper.ToString() : "#";
    }
}
