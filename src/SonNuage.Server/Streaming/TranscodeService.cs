using System.Collections.Concurrent;
using System.Diagnostics;
using SonNuage.Server.Data;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Streaming;

/// <summary>
/// Transcodage à la volée. ffmpeg écrit dans un fichier temporaire du cache. Les clients lisent ce fichier
/// au fur et à mesure qu'il grossit (premier octet en quelques centaines de ms) et plusieurs clients partagent
/// le même encodage. Une fois terminé, le fichier devient une entrée de cache servie avec Range.
/// </summary>
public sealed class TranscodeService : IDisposable
{
    private readonly ConcurrentDictionary<string, TranscodeJob> _jobs = new();
    private readonly SemaphoreSlim _slots;
    private readonly FfmpegLocator _ffmpeg;
    private readonly SonNuageOptions _options;
    private readonly ILogger<TranscodeService> _logger;
    private readonly CancellationToken _stopping;
    private int _completedSinceSweep;

    public TranscodeService(FfmpegLocator ffmpeg, IOptions<SonNuageOptions> options, IHostApplicationLifetime lifetime, ILogger<TranscodeService> logger)
    {
        _ffmpeg = ffmpeg;
        _options = options.Value;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentTranscodes));
    }

    public bool Available => _ffmpeg.Available;

    public string FfmpegPath => _ffmpeg.Path!;

    /// <summary>Créneau d'encodage partagé avec le cache (ne pas saturer le serveur de ffmpeg simultanés).</summary>
    public Task WaitSlotAsync(CancellationToken ct) => _slots.WaitAsync(ct);

    public void ReleaseSlot() => _slots.Release();

    public TranscodeHandle Get(Track track, TranscodeProfile profile)
    {
        var key = $"{track.Id:N}-{profile.Name}-{track.FileModifiedUtc.Ticks:x}-{track.FileSize:x}";
        var dir = Path.Combine(_options.TranscodeCachePath, key[..2]);
        var finalPath = Path.Combine(dir, $"{key}.{profile.Extension}");

        if (File.Exists(finalPath))
        {
            TryTouch(finalPath);
            return new TranscodeHandle(finalPath, null);
        }

        var job = _jobs.GetOrAdd(key, _ => StartJob(key, track, profile, dir, finalPath));
        return new TranscodeHandle(finalPath, job);
    }

    private TranscodeJob StartJob(string key, Track track, TranscodeProfile profile, string dir, string finalPath)
    {
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, $"{key}.{Guid.NewGuid():N}.part");
        var job = new TranscodeJob(tmpPath, finalPath);
        _ = Task.Run(() => RunAsync(key, job, track, profile));
        return job;
    }

    private async Task RunAsync(string key, TranscodeJob job, Track track, TranscodeProfile profile)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(_stopping);
            acquired = true;

            var psi = new ProcessStartInfo(_ffmpeg.Path!)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in profile.FfmpegArguments(track.FilePath, job.TempPath, track.SampleRate)) psi.ArgumentList.Add(arg);

            var sw = Stopwatch.StartNew();
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg n'a pas démarré");
            job.MarkStarted();
            var stderr = process.StandardError.ReadToEndAsync(_stopping);
            _ = process.StandardOutput.ReadToEndAsync(_stopping);

            try
            {
                await process.WaitForExitAsync(_stopping);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg a échoué ({process.ExitCode}) : {(await stderr).Trim()}");

            File.Move(job.TempPath, job.FinalPath, overwrite: true);
            _logger.LogInformation("Transcodé {Track} en {Profile} en {Elapsed} ms", track.Id, profile.Name, sw.ElapsedMilliseconds);
            job.Complete(true);

            if (Interlocked.Increment(ref _completedSinceSweep) >= 20)
            {
                Interlocked.Exchange(ref _completedSinceSweep, 0);
                _ = Task.Run(SweepCache);
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) _logger.LogError(ex, "Transcodage de {Track} en {Profile} impossible", track.Id, profile.Name);
            job.Complete(false);
            TryDelete(job.TempPath);
        }
        finally
        {
            if (acquired) _slots.Release();
            _jobs.TryRemove(key, out _);
        }
    }

    public long CacheSizeBytes()
    {
        var dir = new DirectoryInfo(_options.TranscodeCachePath);
        return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    /// <summary>Purge LRU au-delà de la taille maximale configurée.</summary>
    public void SweepCache()
    {
        var dir = new DirectoryInfo(_options.TranscodeCachePath);
        if (!dir.Exists) return;
        var max = _options.TranscodeCacheMaxMegabytes * 1024 * 1024;
        var files = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToList();

        // Restes d'un arrêt brutal.
        foreach (var part in files.Where(f => f.Extension == ".part" && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
            TryDelete(part.FullName);

        var finished = files.Where(f => f.Extension != ".part" && f.Exists).OrderBy(f => f.LastAccessTimeUtc).ToList();
        var total = finished.Sum(f => f.Length);
        foreach (var f in finished)
        {
            if (total <= max) break;
            total -= f.Length;
            TryDelete(f.FullName);
        }
    }

    public void ClearCache()
    {
        var dir = new DirectoryInfo(_options.TranscodeCachePath);
        if (!dir.Exists) return;
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.Extension != ".part"))
            TryDelete(f.FullName);
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _slots.Dispose();
}

public sealed record TranscodeHandle(string FinalPath, TranscodeJob? Job)
{
    public bool IsCached => Job is null;
}

public sealed class TranscodeJob(string tempPath, string finalPath)
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string TempPath { get; } = tempPath;
    public string FinalPath { get; } = finalPath;
    public Task Started => _started.Task;
    public Task<bool> Completion => _completion.Task;

    internal void MarkStarted() => _started.TrySetResult();

    internal void Complete(bool success)
    {
        _started.TrySetResult();
        _completion.TrySetResult(success);
    }
}
