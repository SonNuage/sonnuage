using System.Data.Common;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Demande l'analyse audio des morceaux en attente : au démarrage, après chaque changement de la bibliothèque (fin de
/// scan, envoi de fichiers) et depuis l'admin. Une seule suite à la fois : elle enchaîne ses lots jusqu'au bout.
/// </summary>
public sealed class AudioAnalysisScheduler(
    IServiceScopeFactory scopes,
    ScanCoordinator scans,
    IAudioAnalyzer analyzer,
    AudioAnalysisSettingsStore settings,
    ILogger<AudioAnalysisScheduler> logger) : BackgroundService
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>L'analyse peut tourner : analyseur installé et réglage activé.</summary>
    public bool Enabled => analyzer.Available && settings.Settings.Enabled;

    /// <summary>Demande une vérification en arrière-plan.</summary>
    public void Request() => _requests.Writer.TryWrite(true);

    /// <summary>Crée la suite d'analyse s'il y a des morceaux en attente et qu'aucune ne tourne déjà. Renvoie vrai si elle est créée.</summary>
    public async Task<bool> RunNowAsync(CancellationToken ct)
    {
        if (!Enabled) return false;
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            if (await AudioAnalysis.ActiveJobs(db).AnyAsync(ct) || !await AudioAnalysis.Pending(db).AnyAsync(ct)) return false;
            await scope.ServiceProvider.GetRequiredService<JobEngine>().EnqueueAsync(db, AudioAnalysisJob.Plan(), ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        scans.LibraryChanged += Request;
        try
        {
            Request();
            await foreach (var _ in _requests.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RunNowAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Analyse audio : demande impossible");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            scans.LibraryChanged -= Request;
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
