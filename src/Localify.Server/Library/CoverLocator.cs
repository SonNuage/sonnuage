using Localify.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Library;

/// <summary>
/// Choisit la pochette d'un album : image « cover/folder » du dossier, sinon image intégrée aux tags,
/// sinon toute image du dossier, sinon image d'un dossier frère de même nom normalisé
/// (« Get a Grip » ⇄ « Get a Grip [Bonus Track] »), sinon image du dossier de l'artiste.
/// </summary>
public sealed class CoverLocator(MetadataReader metadata, IOptions<LocalifyOptions> options)
{
    /// <summary>Pochettes envoyées depuis l'admin : prioritaires, jamais remplacées par le scan.</summary>
    public static string CustomCoverDirectory(LocalifyOptions o) => Path.Combine(o.DataPath, "custom-covers");

    public async Task RefreshAsync(LocalifyDbContext db, IEnumerable<Guid> albumIds, CancellationToken ct)
    {
        foreach (var chunk in albumIds.Distinct().Chunk(200))
        {
            var albums = await db.Albums.Where(a => chunk.Contains(a.Id)).ToListAsync(ct);
            var customDir = Path.GetFullPath(CustomCoverDirectory(options.Value));
            foreach (var album in albums)
            {
                if (album.CoverPath is { } custom && LibraryScanner.IsUnder(Path.GetFullPath(custom), customDir) && File.Exists(custom))
                    continue;

                var trackPaths = await db.Tracks.Where(t => t.AlbumId == album.Id)
                    .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                    .Select(t => t.FilePath).ToListAsync(ct);
                if (trackPaths.Count == 0) continue;

                var (coverPath, embedded) = Locate(trackPaths, album.Title);
                if (coverPath != album.CoverPath || embedded != album.HasEmbeddedCover)
                {
                    album.CoverPath = coverPath;
                    album.HasEmbeddedCover = embedded;
                    album.CoverVersion++;
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    public (string? CoverPath, bool Embedded) Locate(IReadOnlyList<string> trackPaths, string albumTitle)
    {
        var albumDirs = trackPaths.Select(Path.GetDirectoryName).OfType<string>().Distinct().ToList();

        var own = BestImage(albumDirs);
        if (own is { Rank: >= 80 }) return (own.Value.Path, false);

        if (trackPaths.Take(3).Any(p => metadata.ReadEmbeddedPicture(p) is { Length: > 0 }))
            return (null, true);

        if (own is not null) return (own.Value.Path, false);

        var albumKey = NameNormalizer.Key(albumTitle);
        foreach (var dir in albumDirs)
        {
            var artistDir = Path.GetDirectoryName(dir);
            if (artistDir is null) continue;

            var dirKey = NameNormalizer.Key(Path.GetFileName(dir));
            var siblings = SafeDirectories(artistDir)
                .Where(d => d != dir)
                .Where(d => NameNormalizer.Key(Path.GetFileName(d)) is var k && (k == dirKey || k == albumKey))
                .ToList();
            if (BestImage(siblings) is { } sibling) return (sibling.Path, false);

            // Image posée à la racine de l'artiste : seulement s'il n'a qu'un album, sinon elle serait fausse pour les autres.
            var audioDirs = SafeDirectories(artistDir).Count(d => SafeFiles(d).Any(LibraryFiles.IsAudio));
            if (audioDirs <= 1 && BestImage([artistDir]) is { } artistImage) return (artistImage.Path, false);
        }

        return (null, false);
    }

    private static (string Path, int Rank)? BestImage(IEnumerable<string> dirs)
    {
        (string Path, int Rank)? best = null;
        foreach (var dir in dirs)
        {
            foreach (var f in SafeFiles(dir).Where(LibraryFiles.IsImage))
            {
                var rank = LibraryFiles.CoverRank(f);
                if (best is null || rank > best.Value.Rank) best = (f, rank);
            }
        }
        return best;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
