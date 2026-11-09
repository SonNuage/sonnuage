using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Actualise la découverte une fois par semaine (premier passage quelques minutes après le démarrage), et plus tôt
/// s'il reste du travail (titres phares à chercher ou à retrouver sur YouTube), au plus toutes les 6 heures.
/// </summary>
public sealed class DiscoveryScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<DiscoveryScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(7);

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
        var pending = await db.DiscoveryArtists.AnyAsync(a => a.TracksFetchedAt == null, ct)
            || await db.DiscoveryTracks.AnyAsync(t => t.MatchedAt == null, ct);
        if (last is { } l && time.GetUtcNow() - l < (pending ? CheckEvery : RefreshEvery)) return;
        await scopes.RequestAsync(MaintenanceJobs.DiscoveryRefresh(), ct);
    }
}
