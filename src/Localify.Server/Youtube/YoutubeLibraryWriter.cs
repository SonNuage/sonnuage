using System.Diagnostics;
using Localify.Contracts;
using Localify.Server.Admin;
using Localify.Server.Library;
using Localify.Server.Streaming;
using Microsoft.Extensions.Options;

namespace Localify.Server.Youtube;

/// <summary>
/// Range un morceau importé dans <c>Uploads/YouTube/Artiste/Album/NN - Titre [id].ext</c> : conteneur
/// adapté (webm → opus sans réencodage, ou MP3 si demandé), tags complets (identifiants MusicBrainz compris,
/// lien vers la vidéo en commentaire) et pochette (Cover Art Archive, sinon miniature recadrée au carré).
/// </summary>
public sealed class YoutubeLibraryWriter(
    IOptions<LocalifyOptions> options,
    FfmpegLocator ffmpeg,
    MusicBrainzClient musicBrainz,
    MetadataReader metadataReader,
    IHttpClientFactory httpFactory,
    ILogger<YoutubeLibraryWriter> logger)
{
    /// <summary>Convertit au besoin le fichier téléchargé et renvoie le fichier prêt à être tagué.</summary>
    public async Task<string> PrepareAudioAsync(string downloaded, string audioFormat, CancellationToken ct)
    {
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();
        if (audioFormat == "mp3" && ext != ".mp3")
            return await FfmpegAsync(downloaded, ".mp3", ["-vn", "-c:a", "libmp3lame", "-q:a", "0"], ct);
        // L'opus de YouTube arrive dans un conteneur webm, que ni les tags ni tous les lecteurs ne gèrent.
        if (ext is ".webm" or ".mka")
            return await FfmpegAsync(downloaded, ".opus", ["-vn", "-c:a", "copy"], ct);
        return downloaded;
    }

    /// <summary>Tague le fichier, l'installe à sa place définitive (le déplace si besoin) et renvoie son chemin.</summary>
    public async Task<string> WriteAsync(string audioPath, MetadataCandidateDto meta, string videoId, string? thumbnailUrl, CancellationToken ct, Func<string, Task>? beforeMove = null)
    {
        var albumArtist = Clean(meta.AlbumArtist) ?? Clean(meta.Artist) ?? "Artiste inconnu";
        var album = Clean(meta.Album) ?? Clean(meta.Title) ?? "Album inconnu";
        var title = Clean(meta.Title) ?? videoId;

        var cover = await musicBrainz.FrontCoverAsync(meta.ReleaseId, meta.ReleaseGroupId, ct)
            ?? await ThumbnailCoverAsync(thumbnailUrl, ct)
            ?? metadataReader.ReadEmbeddedPicture(audioPath);

        Tag(audioPath, meta, title, albumArtist, album, videoId, cover);

        var dir = Path.Combine(options.Value.YoutubePath, UploadService.SafeSegment(albumArtist), UploadService.SafeSegment(album));
        Directory.CreateDirectory(dir);
        if (cover is not null && !Directory.EnumerateFiles(dir).Any(f => LibraryFiles.IsImage(f)))
            await File.WriteAllBytesAsync(Path.Combine(dir, "cover.jpg"), cover, ct);

        var prefix = meta.TrackNumber is { } n ? $"{(meta.DiscNumber is > 1 ? $"{meta.DiscNumber}-" : "")}{n:00} - " : "";
        var name = UploadService.SafeSegment($"{prefix}{title} [{videoId}]");
        var target = Path.Combine(dir, name + Path.GetExtension(audioPath).ToLowerInvariant());
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(audioPath), StringComparison.OrdinalIgnoreCase))
        {
            var previousDir = Path.GetDirectoryName(audioPath)!;
            if (beforeMove is not null) await beforeMove(target);
            File.Move(audioPath, target, overwrite: true);
            RemoveIfEmpty(previousDir);
        }
        return target;
    }

    private void Tag(string path, MetadataCandidateDto meta, string title, string albumArtist, string album, string videoId, byte[]? cover)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = title;
        tag.Performers = [Clean(meta.Artist) ?? albumArtist];
        tag.AlbumArtists = [albumArtist];
        tag.Album = album;
        tag.Track = (uint)(meta.TrackNumber ?? 0);
        tag.Disc = (uint)(meta.DiscNumber ?? 0);
        tag.Year = (uint)(meta.Year ?? 0);
        tag.Genres = meta.Genre is null ? [] : [meta.Genre];
        tag.Comment = $"https://youtu.be/{videoId}";
        tag.MusicBrainzTrackId = meta.RecordingId;
        tag.MusicBrainzReleaseId = meta.ReleaseId;
        tag.MusicBrainzReleaseGroupId = meta.ReleaseGroupId;
        if (cover is not null)
        {
            tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Cover",
            }];
        }
        file.Save();
    }

    /// <summary>Miniature de la vidéo recadrée au carré (les pochettes YouTube Music le sont déjà).</summary>
    private async Task<byte[]?> ThumbnailCoverAsync(string? url, CancellationToken ct)
    {
        if (url is null || ffmpeg.Path is null) return null;
        var temp = Path.Combine(options.Value.YoutubeDataPath, "tmp", $"thumb-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await File.WriteAllBytesAsync(temp, await httpFactory.CreateClient(YoutubeTools.HttpClientName).GetByteArrayAsync(url, ct), ct);
            var jpg = await FfmpegAsync(temp, ".jpg", ["-vf", "crop='min(iw,ih)':'min(iw,ih)',scale='min(800,iw)':-2", "-frames:v", "1", "-q:v", "3"], ct);
            var bytes = await File.ReadAllBytesAsync(jpg, ct);
            File.Delete(jpg);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            logger.LogDebug("Miniature inutilisable comme pochette : {Message}", ex.Message);
            return null;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<string> FfmpegAsync(string input, string extension, string[] arguments, CancellationToken ct)
    {
        var exe = ffmpeg.Path ?? throw new InvalidOperationException("ffmpeg introuvable.");
        var output = Path.ChangeExtension(input, null) + ".out" + extension;
        var start = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", "-i", input, .. arguments, output]) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Impossible de lancer ffmpeg.");
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            if (File.Exists(output)) File.Delete(output);
            throw new InvalidOperationException($"ffmpeg : {(await errors).Trim()}");
        }
        if (extension != ".jpg") File.Delete(input);
        var final = Path.ChangeExtension(input, null) + extension;
        File.Move(output, final, overwrite: true);
        return final;
    }

    /// <summary>Après déplacement d'un morceau importé, son ancien dossier vide (ou ne contenant que la pochette) disparaît.</summary>
    public void RemoveIfEmpty(string dir)
    {
        var root = Path.GetFullPath(options.Value.YoutubePath);
        while (LibraryScanner.IsUnder(Path.GetFullPath(dir), root) && !string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) return;
            var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            if (entries.Any(e => Directory.Exists(e) || !LibraryFiles.IsImage(e))) return;
            foreach (var image in entries) File.Delete(image);
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
