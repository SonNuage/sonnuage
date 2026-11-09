using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Youtube;

/// <summary>
/// Extraits de 30 secondes d'une vidéo YouTube, pour écouter avant de demander le morceau entier. Chaque extrait est
/// produit une fois (yt-dlp ne télécharge que le passage) puis gardé en cache disque : réécouter ou chercher dans
/// l'extrait est immédiat. Deux productions au plus en même temps, et une seule par vidéo.
/// </summary>
public sealed class PreviewService(YtDlp ytDlp, Storage.StorageGuard storage, IOptions<SonNuageOptions> options, ILogger<PreviewService> logger)
{
    public const int Seconds = 30;
    private const int Parallel = 2;
    private const int MaxCached = 500;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _gate = new(Parallel, Parallel);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _running = new();

    private string Dir => options.Value.PreviewCachePath;

    public string PathFor(string videoId) => Path.Combine(Dir, $"{videoId}.mp3");

    /// <summary>Début de l'extrait : vers le tiers du morceau (on évite l'intro), au début pour un titre court ou de durée inconnue.</summary>
    public static int StartFor(double? duration) =>
        duration is not { } d || d < 60 ? 0 : (int)Math.Min(Math.Round(d * 0.3, MidpointRounding.AwayFromZero), d - Seconds);

    /// <summary>
    /// Chemin de l'extrait, produit au besoin. La production continue même si ce client abandonne : un autre
    /// (ou le même, qui réessaie) en profitera.
    /// </summary>
    public async Task<string> GetAsync(string videoId, double? duration, CancellationToken ct)
    {
        var path = PathFor(videoId);
        if (File.Exists(path)) return path;
        storage.EnsureRoom();
        var lazy = _running.GetOrAdd(videoId, id => new Lazy<Task<string>>(() => ProduceAsync(id, duration, path)));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<string> ProduceAsync(string videoId, double? duration, string path)
    {
        var work = Path.Combine(Dir, "work-" + Guid.NewGuid().ToString("N"));
        await _gate.WaitAsync();
        try
        {
            var produced = await ytDlp.PreviewAsync(videoId, StartFor(duration), Seconds, work, CancellationToken.None);
            File.Move(produced, path, overwrite: true);
            logger.LogInformation("Extrait de {VideoId} prêt", videoId);
            Prune();
            return path;
        }
        finally
        {
            _gate.Release();
            _running.TryRemove(videoId, out _);
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Dossier de travail {Work} non supprimé", work);
            }
        }
    }

    /// <summary>Place occupée par les extraits en cache.</summary>
    public long CachedBytes()
    {
        var dir = new DirectoryInfo(Dir);
        return dir.Exists ? dir.EnumerateFiles("*.mp3").Sum(f => f.Length) : 0;
    }

    /// <summary>
    /// Le cache ne grossit pas sans fin : extraits de plus de 7 jours, au-delà de 500, puis les plus anciens tant que
    /// la place allouée aux extraits (réglage de l'admin) est dépassée.
    /// </summary>
    private void Prune()
    {
        var files = new DirectoryInfo(Dir).EnumerateFiles("*.mp3").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var limit = DateTime.UtcNow - MaxAge;
        var quota = storage.PreviewQuotaBytes;
        long kept = 0;
        foreach (var file in files.Where((f, i) => i >= MaxCached || f.LastWriteTimeUtc < limit || (kept += f.Length) > quota))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // En cours de lecture : on réessaiera au prochain extrait.
            }
        }
    }
}
