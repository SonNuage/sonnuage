using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SonNuage.Server.Streaming;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Pochettes des parutions (Cover Art Archive, par groupe de parutions), lues une fois par le serveur et gardées sur
/// disque : les applis ne contactent jamais de tiers. Une parution sans pochette n'est redemandée qu'au bout de 7 jours.
/// </summary>
public sealed class ReleaseCoverCache(MusicBrainzClient musicBrainz, IOptions<SonNuageOptions> options, ILogger<ReleaseCoverCache> logger)
{
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static readonly string[] Extensions = [".jpg", ".png", ".webp"];

    private string Directory => Path.Combine(options.Value.CoverCachePath, "release-groups");

    /// <summary>Chemin de la pochette en cache (lue au besoin) ; null si la parution n'en a pas ou si l'archive ne répond pas.</summary>
    public async Task<string?> GetAsync(Guid releaseGroupId, CancellationToken ct)
    {
        if (Cached(releaseGroupId) is { } hit) return hit;
        var gate = Locks.GetOrAdd(releaseGroupId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Cached(releaseGroupId) is { } again) return again;
            var missing = Path.Combine(Directory, $"{releaseGroupId:N}.none");
            if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < RetryMissingAfter) return null;

            byte[]? data;
            try
            {
                data = await musicBrainz.ReleaseGroupCoverAsync(releaseGroupId, ct);
            }
            catch (HttpRequestException ex)
            {
                // Erreur passagère : rien n'est retenu, on réessaiera.
                logger.LogDebug("Cover Art Archive : {Message}", ex.Message);
                return null;
            }
            System.IO.Directory.CreateDirectory(Directory);
            if (data is not { Length: > 0 })
            {
                await File.WriteAllBytesAsync(missing, [], ct);
                return null;
            }
            var path = Path.Combine(Directory, $"{releaseGroupId:N}{CoverService.SniffExtension(data)}");
            await File.WriteAllBytesAsync(path, data, ct);
            File.Delete(missing);
            return path;
        }
        finally
        {
            gate.Release();
        }
    }

    private string? Cached(Guid releaseGroupId) =>
        Extensions.Select(ext => Path.Combine(Directory, $"{releaseGroupId:N}{ext}")).FirstOrDefault(File.Exists);
}
