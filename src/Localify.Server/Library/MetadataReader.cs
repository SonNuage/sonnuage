namespace Localify.Server.Library;

public sealed record AudioMetadata(
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    string? Genre,
    double DurationSeconds,
    int? BitrateKbps,
    int? SampleRate,
    bool HasEmbeddedPicture);

/// <summary>
/// Lit les tags avec TagLib#. Ce qui manque est déduit de l'arborescence
/// <c>Artiste/Album/NN Titre.ext</c> relative à la racine de la bibliothèque.
/// </summary>
public sealed class MetadataReader(ILogger<MetadataReader> logger)
{
    public AudioMetadata Read(string path, string libraryRoot)
    {
        var (folderArtist, folderAlbum) = GuessFromFolders(path, libraryRoot);

        string? title = null, artist = null, albumArtist = null, album = null, genre = null;
        int? track = null, disc = null, year = null, bitrate = null, sampleRate = null;
        double duration = 0;
        var hasPicture = false;

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            title = NullIfBlank(tag.Title);
            artist = NullIfBlank(tag.FirstPerformer) ?? NullIfBlank(tag.JoinedPerformers);
            albumArtist = NullIfBlank(tag.FirstAlbumArtist);
            album = NullIfBlank(tag.Album);
            genre = NullIfBlank(tag.FirstGenre);
            track = tag.Track > 0 ? (int)tag.Track : null;
            disc = tag.Disc > 0 ? (int)tag.Disc : null;
            year = tag.Year is > 0 and < 3000 ? (int)tag.Year : null;
            hasPicture = tag.Pictures.Length > 0;

            if (file.Properties is { } props)
            {
                duration = props.Duration.TotalSeconds;
                bitrate = props.AudioBitrate > 0 ? props.AudioBitrate : null;
                sampleRate = props.AudioSampleRate > 0 ? props.AudioSampleRate : null;
            }
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
        {
            logger.LogDebug(ex, "Tags illisibles pour {Path}, repli sur le nom de fichier", path);
        }

        artist ??= albumArtist ?? folderArtist ?? "Artiste inconnu";
        albumArtist ??= folderArtist ?? artist;
        album ??= folderAlbum ?? "Album inconnu";
        title ??= NameNormalizer.CleanTitle(Path.GetFileName(path), $"{artist} - {album} - ", $"{albumArtist} - {album} - ", $"{artist} - ");
        track ??= NameNormalizer.TrackNumberFromFileName(path);

        return new AudioMetadata(title, artist, albumArtist, album, track, disc, year, genre, duration, bitrate, sampleRate, hasPicture);
    }

    public byte[]? ReadEmbeddedPicture(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures;
            if (pictures.Length == 0) return null;
            var front = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
            return front.Data.Data;
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
        {
            return null;
        }
    }

    private static (string? Artist, string? Album) GuessFromFolders(string path, string libraryRoot)
    {
        var relative = Path.GetRelativePath(libraryRoot, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        // parts = [Artiste, Album, ..., fichier]
        return parts.Length switch
        {
            >= 3 => (NameNormalizer.CleanTitle(parts[0]), NameNormalizer.CleanTitle(parts[^2])),
            2 => (NameNormalizer.CleanTitle(parts[0]), null),
            _ => (null, null),
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
