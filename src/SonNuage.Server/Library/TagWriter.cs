using SonNuage.Server.Data;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Library;

/// <summary>
/// Reporte dans les tags des fichiers les métadonnées corrigées en base (édition dans l'admin, rescan AcoustID).
/// Seuls les fichiers d'<c>uploads/</c> sont réécrits : la bibliothèque principale (<c>music/</c>) est montée en lecture
/// seule, seule la base change pour elle. Écriture atomique : une copie temporaire est taguée dans le même dossier,
/// puis renommée par-dessus l'original. TagLib# ne réécrit que les tags : le flux audio reste tel quel.
/// </summary>
public sealed class TagWriter(IOptions<SonNuageOptions> options, ILogger<TagWriter> logger)
{
    private string UploadRoot => Path.GetFullPath(options.Value.UploadPath);

    /// <summary>Le fichier peut-il recevoir des tags ? (seuls les envois, jamais la bibliothèque en lecture seule)</summary>
    public bool IsWritable(string path) => LibraryScanner.IsUnder(path, UploadRoot);

    /// <summary>Réécrit les tags des morceaux d'<c>uploads/</c> et met à jour leur taille et leur date en base (à enregistrer).</summary>
    public void Write(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks.Where(t => IsWritable(t.FilePath) && File.Exists(t.FilePath))) Write(track);
    }

    private void Write(Track track)
    {
        var path = track.FilePath;
        // Nom caché et extension non audio : ni le scan ni la surveillance des dossiers ne le prennent pour un morceau.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(path, temp);
            using (var file = TagLib.File.Create(new TagLib.File.LocalFileAbstraction(temp), MimeType(path), TagLib.ReadStyle.Average))
            {
                Apply(file.Tag, track);
                file.Save();
            }
            // TagLib# laisse parfois le paquet de tags Ogg non terminé (voir OggRepair).
            if (OggRepair.IsOgg(path)) OggRepair.Repair(temp);
            File.Move(temp, path, overwrite: true);
            var info = new FileInfo(path);
            track.FileSize = info.Length;
            track.FileModifiedUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException
                                       or ArgumentException or NotImplementedException)
        {
            // La base reste la référence : le fichier garde ses anciens tags.
            logger.LogWarning("Tags non réécrits pour {Path} : {Message}", path, ex.Message);
            TryDelete(temp);
        }
    }

    private static void Apply(TagLib.Tag tag, Track t)
    {
        tag.Title = t.Title;
        tag.Performers = [t.ArtistName];
        tag.AlbumArtists = [t.AlbumArtistName];
        tag.Album = t.AlbumTitle;
        tag.Track = (uint)(t.TrackNumber ?? 0);
        tag.Disc = (uint)(t.DiscNumber ?? 0);
        tag.Year = (uint)(t.Year ?? 0);
        tag.Genres = t.Genre is null ? [] : [t.Genre];
        tag.MusicBrainzTrackId = t.MusicBrainzRecordingId?.ToString();
        tag.MusicBrainzReleaseId = t.MusicBrainzReleaseId?.ToString();
    }

    /// <summary>Type TagLib# du fichier d'origine (la copie temporaire n'a pas d'extension audio).</summary>
    private static string MimeType(string path) => "taglib/" + Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
