using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Api;

/// <summary>Tableau de bord de l'admin : provenance des morceaux, historique des ajouts, occupation du disque.</summary>
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/dashboard", async (string? range, int? offsetMinutes, SonNuageDbContext db, IOptions<SonNuageOptions> options, CancellationToken ct) =>
        {
            var o = options.Value;
            var youtubeRoot = Path.GetFullPath(o.YoutubePath);
            var uploadRoot = Path.GetFullPath(o.UploadPath);
            string SourceOf(string path) =>
                LibraryScanner.IsUnder(path, youtubeRoot) ? "youtube" : LibraryScanner.IsUnder(path, uploadRoot) ? "upload" : "library";

            var tracks = (await db.Tracks.AsNoTracking().Select(t => new { t.FilePath, t.CreatedAt, t.DurationSeconds, t.FileSize }).ToListAsync(ct))
                .Select(t => (Source: SourceOf(t.FilePath), t.CreatedAt, t.DurationSeconds, t.FileSize))
                .ToList();
            var sources = new[] { "library", "upload", "youtube" }
                .Select(s => tracks.Where(t => t.Source == s).ToList())
                .Select((list, i) => new SourceTotalDto(new[] { "library", "upload", "youtube" }[i], list.Count, list.Sum(t => t.DurationSeconds), list.Sum(t => t.FileSize)))
                .ToList();

            var offset = TimeSpan.FromMinutes(Math.Clamp(offsetMinutes ?? 0, -14 * 60, 14 * 60));
            var (key, buckets) = Buckets(range, DateTimeOffset.UtcNow.ToOffset(offset), tracks.Count == 0 ? null : tracks.Min(t => t.CreatedAt).ToOffset(offset));
            var history = buckets.Select((start, i) =>
            {
                var end = i + 1 < buckets.Count ? buckets[i + 1] : DateTimeOffset.MaxValue;
                var inBucket = tracks.Where(t => t.CreatedAt >= start && t.CreatedAt < end).ToList();
                return new ImportBucketDto(start, inBucket.Count(t => t.Source == "library"), inBucket.Count(t => t.Source == "upload"), inBucket.Count(t => t.Source == "youtube"));
            }).ToList();

            return new DashboardDto(
                await db.Artists.CountAsync(a => a.Albums.Any(), ct),
                await db.Albums.CountAsync(ct),
                tracks.Count,
                await db.Playlists.CountAsync(ct),
                await db.Users.CountAsync(ct),
                sources,
                history,
                key,
                Storage(o, sources));
        }).RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");
    }

    /// <summary>Débuts des intervalles : 24 heures, 7 ou 30 jours, 12 mois, ou tout depuis le premier ajout (mois, années au-delà de 10 ans).</summary>
    internal static (string Key, List<DateTimeOffset> Starts) Buckets(string? range, DateTimeOffset now, DateTimeOffset? first)
    {
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        switch (range)
        {
            case "day":
                var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
                return ("day", Enumerable.Range(0, 24).Select(i => hour.AddHours(i - 23)).ToList());
            case "week":
                return ("week", Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToList());
            case "year":
                return ("year", Enumerable.Range(0, 12).Select(i => month.AddMonths(i - 11)).ToList());
            case "all":
                var start = first ?? now;
                var months = (now.Year - start.Year) * 12 + now.Month - start.Month + 1;
                if (months > 120)
                {
                    var years = now.Year - start.Year + 1;
                    return ("all", Enumerable.Range(0, years).Select(i => new DateTimeOffset(start.Year + i, 1, 1, 0, 0, 0, now.Offset)).ToList());
                }
                return ("all", Enumerable.Range(0, Math.Max(months, 1)).Select(i => month.AddMonths(i - Math.Max(months, 1) + 1)).ToList());
            default:
                return ("month", Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).ToList());
        }
    }

    /// <summary>Point de montage vu par le serveur : racine, système de fichiers, taille et espace libre.</summary>
    public sealed record Disk(string Root, string Format, long TotalBytes, long FreeBytes);

    // Écart d'espace libre toléré entre deux lectures du même disque (une copie peut tourner entre les deux).
    private const long FreeSpaceDrift = 1L << 30;

    /// <summary>
    /// Même disque physique ? Dans un conteneur, /music et /uploads sont deux points de montage distincts d'un même
    /// système de fichiers : ils ont alors la même taille, le même format et (presque) le même espace libre.
    /// </summary>
    public static bool SameDisk(Disk a, Disk b) =>
        a.Root == b.Root
        || (a.Format == b.Format && a.TotalBytes == b.TotalBytes && Math.Abs(a.FreeBytes - b.FreeBytes) < FreeSpaceDrift);

    /// <summary>Disque qui accueille les envois et imports, découpé en musique (par provenance), caches, autres fichiers et libre.</summary>
    private static StorageDto? Storage(SonNuageOptions o, IReadOnlyList<SourceTotalDto> sources)
    {
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
        Disk? DiskOf(string path)
        {
            var full = Path.GetFullPath(path);
            return drives
                .Where(d => full.StartsWith(d.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length) is { } d
                ? new Disk(d.RootDirectory.FullName, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace)
                : null;
        }

        try
        {
            if (DiskOf(o.UploadPath) is not { } disk) return null;
            bool OnDisk(string path) => DiskOf(path) is { } other && SameDisk(disk, other);

            var slices = new List<StorageSliceDto>();
            var bytes = sources.ToDictionary(s => s.Source, s => s.Bytes);
            if (o.MusicPaths.Any(p => !string.IsNullOrWhiteSpace(p) && OnDisk(p))) slices.Add(new("library", bytes["library"]));
            slices.Add(new("upload", bytes["upload"]));
            slices.Add(new("youtube", bytes["youtube"]));
            if (OnDisk(o.DataPath))
            {
                var cache = new DirectoryInfo(Path.Combine(o.DataPath, "cache"));
                slices.Add(new("cache", cache.Exists ? cache.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0));
            }
            var used = disk.TotalBytes - disk.FreeBytes;
            slices.Add(new("other", Math.Max(0, used - slices.Sum(s => s.Bytes))));
            slices.Add(new("free", disk.FreeBytes));
            return new StorageDto(disk.Root, disk.TotalBytes, disk.FreeBytes, slices);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
