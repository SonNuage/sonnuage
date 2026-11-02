using System.Collections.Concurrent;
using System.Threading.Channels;
using SonNuage.Contracts;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Library;

/// <summary>
/// Sérialise les scans (un seul à la fois), expose leur état à l'admin et regroupe
/// les événements du FileSystemWatcher pour ne réindexer qu'une fois les copies terminées.
/// </summary>
public sealed class ScanCoordinator(
    IServiceScopeFactory scopes,
    IOptions<SonNuageOptions> options,
    ILogger<ScanCoordinator> logger) : BackgroundService
{
    private readonly Channel<bool> _fullScanRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, DateTime> _changedFiles = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private volatile ScanStatusDto _status = new(false, null, null, 0, 0, 0, 0, null);
    // Surveillance débordée (copie massive) : le scan complet attend que l'activité retombe, sinon il se relance
    // à chaque débordement pendant toute la copie.
    private static readonly TimeSpan QuietBeforeFullScan = TimeSpan.FromSeconds(60);
    private volatile bool _overflowed;
    private long _lastEventTicks;

    public ScanStatusDto Status => _status;

    /// <summary>La bibliothèque a pu changer (fin de scan complet ou partiel).</summary>
    public event Action? LibraryChanged;

    public void RequestFullScan() => _fullScanRequests.Writer.TryWrite(true);

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Filet de sécurité, chaque heure (en plus des purges faites après chaque scan ou suppression) : albums restés
    /// sans aucun titre et artistes sans album ni titre disparaissent, l'index de recherche suit.
    /// </summary>
    public async Task<int> PurgeEmptyAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var removed = await LibraryIndexer.PruneAsync(scope.ServiceProvider.GetRequiredService<Data.SonNuageDbContext>(), ct);
            if (removed > 0)
            {
                logger.LogInformation("{Count} album(s) ou artiste(s) vide(s) supprimé(s)", removed);
                LibraryChanged?.Invoke();
            }
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Indexe immédiatement des fichiers (upload) en attendant la fin d'un scan éventuel.</summary>
    public async Task<IReadOnlyList<Data.Track>> ScanFilesNowAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
            var tracks = await scanner.ScanFilesAsync(files, ct);
            LibraryChanged?.Invoke();
            return tracks;
        }
        finally
        {
            _lock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartWatchers();
        RequestFullScan();

        var interval = options.Value.ScanIntervalMinutes > 0 ? TimeSpan.FromMinutes(options.Value.ScanIntervalMinutes) : Timeout.InfiniteTimeSpan;
        var nextPeriodic = DateTime.UtcNow + interval;
        var nextPurge = DateTime.UtcNow + PurgeInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tick.CancelAfter(TimeSpan.FromSeconds(5));
            var full = false;
            try
            {
                full = await _fullScanRequests.Reader.ReadAsync(tick.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }

            if (_overflowed && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastEventTicks), DateTimeKind.Utc) >= QuietBeforeFullScan)
            {
                _overflowed = false;
                full = true;
            }

            if (interval != Timeout.InfiniteTimeSpan && DateTime.UtcNow >= nextPeriodic)
            {
                full = true;
                nextPeriodic = DateTime.UtcNow + interval;
            }

            try
            {
                if (full) await RunFullScanAsync(stoppingToken);
                else await FlushChangedFilesAsync(stoppingToken);
                if (DateTime.UtcNow >= nextPurge)
                {
                    nextPurge = DateTime.UtcNow + PurgeInterval;
                    await PurgeEmptyAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du scan");
                _status = _status with { Running = false, LastError = ex.Message, LastFinishedAt = DateTimeOffset.UtcNow };
            }
        }
    }

    private async Task RunFullScanAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _changedFiles.Clear();
            _status = new ScanStatusDto(true, DateTimeOffset.UtcNow, _status.LastFinishedAt, 0, 0, 0, 0, null);
            using var scope = scopes.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
            var progress = new Progress<ScanResult>(p => _status = _status with { FilesSeen = p.FilesSeen, Added = p.Added, Updated = p.Updated });
            var r = await scanner.ScanAllAsync(progress, ct);
            _status = _status with
            {
                Running = false,
                LastFinishedAt = DateTimeOffset.UtcNow,
                FilesSeen = r.FilesSeen,
                Added = r.Added,
                Updated = r.Updated,
                Removed = r.Removed,
            };
            LibraryChanged?.Invoke();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task FlushChangedFilesAsync(CancellationToken ct)
    {
        // On attend 10 s sans nouvel événement sur un fichier : une copie de gros WAV prend du temps.
        var quietSince = DateTime.UtcNow.AddSeconds(-10);
        var ready = _changedFiles.Where(kv => kv.Value < quietSince).Select(kv => kv.Key).ToList();
        if (ready.Count == 0) return;
        foreach (var f in ready) _changedFiles.TryRemove(f, out _);

        var files = new List<string>();
        foreach (var path in ready)
        {
            if (!Directory.Exists(path))
            {
                files.Add(path);
                continue;
            }
            try
            {
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(LibraryFiles.IsAudio));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Dossier encore en cours de copie (droits posés à la fin par tar, rsync…) : on y reviendra.
                // Sans toucher à l'horodatage d'activité : un dossier durablement illisible ne doit pas bloquer le scan complet.
                logger.LogDebug("Dossier {Path} illisible pour l'instant : {Message}", path, ex.Message);
                _changedFiles[path] = DateTime.UtcNow;
            }
        }
        if (files.Count == 0) return;

        // Un dossier supprimé ou renommé : le plus simple et le plus sûr est un scan complet (incrémental, donc rapide).
        if (files.Any(f => !File.Exists(f) && !LibraryFiles.IsAudio(f)))
        {
            RequestFullScan();
            return;
        }

        logger.LogInformation("Réindexation de {Count} fichier(s) modifié(s)", files.Count);
        await ScanFilesNowAsync(files, ct);
    }

    private void StartWatchers()
    {
        if (!options.Value.WatchFileSystem) return;
        using var scope = scopes.CreateScope();
        foreach (var root in scope.ServiceProvider.GetRequiredService<LibraryScanner>().Roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => Touch(e.FullPath);
                w.Changed += (_, e) => Touch(e.FullPath);
                w.Deleted += (_, e) => Touch(e.FullPath);
                w.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
                w.Error += (_, e) =>
                {
                    Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
                    if (_overflowed) return;
                    _overflowed = true;
                    logger.LogWarning(e.GetException(), "FileSystemWatcher saturé : scan complet dès {Quiet} s sans activité", QuietBeforeFullScan.TotalSeconds);
                };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                // Montages réseau (SMB, NFS) : souvent pas de notifications. Le scan périodique prend le relais.
                logger.LogWarning(ex, "Surveillance impossible de {Root}", root);
            }
        }
    }

    private void Touch(string path)
    {
        Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
        if (LibraryFiles.IsAudio(path) || !Path.HasExtension(path))
            _changedFiles[path] = DateTime.UtcNow;
    }

    public override void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _lock.Dispose();
        base.Dispose();
    }
}
