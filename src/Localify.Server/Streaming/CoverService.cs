using System.Collections.Concurrent;
using System.Diagnostics;
using Localify.Server.Data;
using Localify.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Streaming;

/// <summary>Pochettes : image du dossier ou extraite des tags, redimensionnée par ffmpeg et mise en cache.</summary>
public sealed class CoverService(
    MetadataReader metadata,
    FfmpegLocator ffmpeg,
    IOptions<LocalifyOptions> options,
    ILogger<CoverService> logger)
{
    public static readonly int[] AllowedSizes = [96, 300, 600];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public async Task<(string Path, string ContentType)?> GetAsync(LocalifyDbContext db, Album album, int? size, CancellationToken ct)
    {
        var source = await SourceAsync(db, album, ct);
        if (source is null) return null;

        var target = size is { } s ? AllowedSizes.FirstOrDefault(a => a >= s, AllowedSizes[^1]) : (int?)null;
        if (target is null || !ffmpeg.Available) return (source, LibraryFiles.ContentType(source));

        var thumb = Path.Combine(options.Value.CoverCachePath, $"{album.Id:N}-{album.CoverVersion}-{target}.jpg");
        if (File.Exists(thumb)) return (thumb, "image/jpeg");

        var gate = Locks.GetOrAdd(thumb, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(thumb) && !await ResizeAsync(source, thumb, target.Value, ct))
                return (source, LibraryFiles.ContentType(source));
            return (thumb, "image/jpeg");
        }
        finally
        {
            gate.Release();
            Locks.TryRemove(thumb, out _);
        }
    }

    private async Task<string?> SourceAsync(LocalifyDbContext db, Album album, CancellationToken ct)
    {
        if (album.CoverPath is { } p && File.Exists(p)) return p;
        if (!album.HasEmbeddedCover) return null;

        var prefix = Path.Combine(options.Value.CoverCachePath, $"{album.Id:N}-{album.CoverVersion}-embedded");
        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
        {
            if (File.Exists(prefix + ext)) return prefix + ext;
        }

        var paths = await db.Tracks.Where(t => t.AlbumId == album.Id)
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .Select(t => t.FilePath).Take(5).ToListAsync(ct);
        foreach (var path in paths)
        {
            if (metadata.ReadEmbeddedPicture(path) is { Length: > 0 } data)
            {
                Directory.CreateDirectory(options.Value.CoverCachePath);
                var extracted = prefix + SniffExtension(data);
                await File.WriteAllBytesAsync(extracted, data, ct);
                return extracted;
            }
        }
        return null;
    }

    private static string SniffExtension(byte[] data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        _ => ".jpg",
    };

    private async Task<bool> ResizeAsync(string source, string target, int size, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp.jpg";
        var psi = new ProcessStartInfo(ffmpeg.Path!)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", source, "-frames:v", "1",
                     "-vf", $"scale='min({size},iw)':-1", "-q:v", "3", "-y", tmp })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process is null) return false;
        var err = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(tmp))
        {
            logger.LogWarning("Miniature impossible pour {Source} : {Error}", source, err.Trim());
            return false;
        }
        File.Move(tmp, target, overwrite: true);
        return true;
    }
}
