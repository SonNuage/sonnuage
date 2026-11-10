using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Library;

public sealed record ScanResult(int FilesSeen, int Added, int Updated, int Removed);

/// <summary>
/// Scan incrémental : un fichier dont la taille et la date n'ont pas changé n'est pas relu.
/// Les IDs de pistes sont stables tant que le chemin du fichier ne change pas.
/// </summary>
public sealed class LibraryScanner(
    SonNuageDbContext db,
    MetadataReader metadata,
    CoverLocator covers,
    IOptions<SonNuageOptions> options,
    ILogger<LibraryScanner> logger)
{
    private const int BatchSize = 250;

    public IReadOnlyList<string> Roots => RootsOf(options.Value);

    /// <summary>Dossiers de la bibliothèque (musique en lecture seule puis envois), chemins absolus, sans doublon.</summary>
    public static List<string> RootsOf(SonNuageOptions o) =>
        o.MusicPaths.Append(o.UploadPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public async Task<ScanResult> ScanAllAsync(IProgress<ScanResult>? progress, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var indexer = new LibraryIndexer(db);
        await indexer.LoadAsync(ct);

        var known = await db.Tracks.AsNoTracking()
            .Select(t => new { t.Id, t.FilePath, t.FileSize, t.FileModifiedUtc })
            .ToDictionaryAsync(t => t.FilePath, ct);
        var hidden = await HiddenAsync(ct);

        int seen = 0, added = 0, updated = 0, pending = 0;
        var unchanged = new List<Guid>();
        var touched = new HashSet<Guid>();

        foreach (var root in Roots)
        {
            if (!Directory.Exists(root))
            {
                logger.LogWarning("Dossier de musique introuvable : {Root}", root);
                continue;
            }

            foreach (var file in EnumerateAudio(root))
            {
                ct.ThrowIfCancellationRequested();
                // Doublon masqué depuis l'admin (fichier en lecture seule) : plus indexé.
                if (hidden.Contains(file)) continue;
                seen++;
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (IOException) { continue; }

                if (known.TryGetValue(file, out var k) && k.FileSize == info.Length && k.FileModifiedUtc == info.LastWriteTimeUtc)
                {
                    unchanged.Add(k.Id);
                }
                else
                {
                    var isNew = !known.ContainsKey(file);
                    if (await IndexFileAsync(indexer, file, root, info, ct) is not null)
                    {
                        if (isNew) added++; else updated++;
                        pending++;
                    }
                }

                if (pending >= BatchSize)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    touched.UnionWith(indexer.TouchedAlbums);
                    indexer = new LibraryIndexer(db);
                    await indexer.LoadAsync(ct);
                    pending = 0;
                }
                if (seen % 500 == 0) progress?.Report(new ScanResult(seen, added, updated, 0));
            }
        }

        await indexer.RelinkCreditsAsync(ct);
        await db.SaveChangesAsync(ct);

        foreach (var chunk in unchanged.Chunk(500))
            await db.Tracks.Where(t => chunk.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastSeenAt, started), ct);

        // Pistes non revues : fichiers supprimés, renommés ou désormais ignorés (doublons). On ne supprime que sous
        // les racines présentes, pour ne pas vider la bibliothèque si un disque n'est pas monté.
        var presentRoots = Roots.Where(Directory.Exists).ToList();
        var stale = await db.Tracks.Where(t => t.LastSeenAt < started).Select(t => new { t.Id, t.FilePath }).ToListAsync(ct);
        var toRemove = stale.Where(t => presentRoots.Any(r => IsUnder(t.FilePath, r))).ToList();
        foreach (var chunk in toRemove.Chunk(500))
        {
            var ids = chunk.Select(c => c.Id).ToList();
            await db.Tracks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct);
        }
        await LibraryIndexer.PruneAsync(db, ct);

        touched.UnionWith(indexer.TouchedAlbums);
        var albumsWithoutCover = await db.Albums.Where(a => a.CoverPath == null && !a.HasEmbeddedCover).Select(a => a.Id).ToListAsync(ct);
        touched.UnionWith(albumsWithoutCover);
        await covers.RefreshAsync(db, touched, ct);

        var result = new ScanResult(seen, added, updated, toRemove.Count);
        logger.LogInformation("Scan terminé : {Seen} fichiers, {Added} ajoutés, {Updated} mis à jour, {Removed} supprimés", seen, added, updated, toRemove.Count);
        return result;
    }

    /// <summary>Indexe (ou réindexe) des fichiers précis : upload, changement détecté par le watcher.</summary>
    public async Task<IReadOnlyList<Track>> ScanFilesAsync(IEnumerable<string> files, CancellationToken ct)
    {
        var indexer = new LibraryIndexer(db);
        await indexer.LoadAsync(ct);
        var result = new List<Track>();
        var removedAny = false;
        var hidden = await HiddenAsync(ct);

        foreach (var file in files.Select(Path.GetFullPath).Distinct())
        {
            var root = Roots.FirstOrDefault(r => IsUnder(file, r));
            if (root is null || hidden.Contains(file)) continue;

            if (!File.Exists(file))
            {
                removedAny |= await db.Tracks.Where(t => t.FilePath == file).ExecuteDeleteAsync(ct) > 0;
                continue;
            }
            if (!LibraryFiles.IsAudio(file) || LibraryFiles.IsDuplicateCopy(file)) continue;

            if (await IndexFileAsync(indexer, file, root, new FileInfo(file), ct) is { } track) result.Add(track);
        }

        await db.SaveChangesAsync(ct);
        if (removedAny || indexer.TouchedAlbums.Count > 0) await LibraryIndexer.PruneAsync(db, ct);
        await covers.RefreshAsync(db, indexer.TouchedAlbums, ct);
        return result;
    }

    private async Task<Track?> IndexFileAsync(LibraryIndexer indexer, string file, string root, FileInfo info, CancellationToken ct)
    {
        AudioMetadata meta;
        try
        {
            meta = metadata.Read(file, root);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lecture impossible : {File}", file);
            return null;
        }

        var track = await db.Tracks.FirstOrDefaultAsync(t => t.FilePath == file, ct);
        if (track is null)
        {
            track = new Track
            {
                FilePath = file,
                Title = meta.Title,
                ArtistName = meta.Artist,
                AlbumArtistName = meta.AlbumArtist,
                AlbumTitle = meta.Album,
                Format = LibraryFiles.Format(file),
                IsLossless = LibraryFiles.IsLossless(file),
            };
            db.Tracks.Add(track);
        }

        track.FileSize = info.Length;
        track.FileModifiedUtc = info.LastWriteTimeUtc;
        track.LastSeenAt = DateTimeOffset.UtcNow;
        track.Format = LibraryFiles.Format(file);
        track.IsLossless = LibraryFiles.IsLossless(file);
        indexer.ApplyMetadata(track, meta);
        indexer.Assign(track, meta);
        return track;
    }

    private async Task<HashSet<string>> HiddenAsync(CancellationToken ct) =>
        (await db.HiddenFiles.Select(h => h.Path).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    private IEnumerable<string> EnumerateAudio(string root)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };
        return Directory.EnumerateFiles(root, "*", enumeration)
            .Where(LibraryFiles.IsAudio)
            .Where(f => !LibraryFiles.IsDuplicateCopy(f));
    }

    internal static bool IsUnder(string path, string root)
    {
        var r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
