using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Analysis;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

/// <summary>Analyse audio dans l'admin : avancement, activation, lancement immédiat.</summary>
public static class AudioAnalysisEndpoints
{
    public static void MapAudioAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/analysis").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");

        g.MapGet("", (SonNuageDbContext db, IAudioAnalyzer analyzer, AudioAnalysisSettingsStore store, CancellationToken ct) =>
            AudioAnalysis.StatusAsync(db, analyzer.Available, store.Settings.Enabled, ct));

        g.MapPut("/settings", (AudioAnalysisSettingsDto s, SonNuageDbContext db, IAudioAnalyzer analyzer, AudioAnalysisSettingsStore store,
            AudioAnalysisScheduler scheduler, CancellationToken ct) =>
        {
            store.Save(s);
            if (s.Enabled) scheduler.Request();
            return AudioAnalysis.StatusAsync(db, analyzer.Available, store.Settings.Enabled, ct);
        });

        g.MapPost("/run", async (SonNuageDbContext db, IAudioAnalyzer analyzer, AudioAnalysisSettingsStore store,
            AudioAnalysisScheduler scheduler, CancellationToken ct) =>
        {
            if (!analyzer.Available) return Results.Problem(Tx.Key("errors.analysis.unavailable"), statusCode: StatusCodes.Status409Conflict);
            if (!store.Settings.Enabled) return Results.Problem(Tx.Key("errors.analysis.disabled"), statusCode: StatusCodes.Status409Conflict);
            await scheduler.RunNowAsync(ct);
            return Results.Accepted(value: await AudioAnalysis.StatusAsync(db, analyzer.Available, store.Settings.Enabled, ct));
        });
    }
}
