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
    /// <summary>Ouvre les tags ; un Ogg abîmé par TagLib (voir <see cref="OggRepair"/>) est réparé puis relu, s'il est modifiable.</summary>
    private TagLib.File OpenTags(string path)
    {
        try
        {
            return TagLib.File.Create(path);
        }
        catch (Exception ex) when (OggRepair.IsOgg(path) && ex is not TagLib.UnsupportedFormatException && TryRepair(path))
        {
            return TagLib.File.Create(path);
        }
    }

    private bool TryRepair(string path)
    {
        try
        {
            if (!OggRepair.Repair(path)) return false;
            logger.LogInformation("Fichier Ogg réparé (paquet de tags non terminé) : {Path}", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bibliothèque en lecture seule : on se contente du repli sur le nom de fichier.
            logger.LogDebug("Réparation impossible pour {Path} : {Message}", path, ex.Message);
            return false;
        }
    }

    public AudioMetadata Read(string path, string libraryRoot)
    {
        var (folderArtist, folderAlbum) = GuessFromFolders(path, libraryRoot);

        string? title = null, artist = null, albumArtist = null, album = null, genre = null;
        int? track = null, disc = null, year = null, bitrate = null, sampleRate = null;
        double duration = 0;
        var hasPicture = false;

        try
        {
            using var file = OpenTags(path);
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
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException
                                       or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            // Le morceau reste dans la bibliothèque, décrit par son dossier et son nom de fichier.
            logger.LogWarning("Tags illisibles pour {Path}, repli sur le nom de fichier : {Message}", path, ex.Message);
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
