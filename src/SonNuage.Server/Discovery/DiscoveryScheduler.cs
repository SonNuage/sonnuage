using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Complète la découverte toutes les 6 heures (premier passage quelques minutes après le démarrage) : chaque
/// actualisation ajoute de nouveaux artistes et retire ceux de plus de 7 jours.
/// </summary>
public sealed class DiscoveryScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<DiscoveryScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Actualisation de la découverte non planifiée");
                }
                await Task.Delay(CheckEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        if (!await db.Tracks.AnyAsync(ct)) return;
        var last = await db.Jobs.Where(j => j.Kind == JobKinds.DiscoveryRefresh.Name).MaxAsync(j => (DateTimeOffset?)j.NotBefore, ct);
        if (last is { } l && time.GetUtcNow() - l < RefreshEvery) return;
        await scopes.RequestAsync(MaintenanceJobs.DiscoveryRefresh(), ct);
    }
}
