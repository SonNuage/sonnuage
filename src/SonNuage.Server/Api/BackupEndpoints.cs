using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Storage;

namespace SonNuage.Server.Api;

/// <summary>Réglages → Sauvegardes : état, réglages et sauvegarde immédiate (tâche du volet Tâches).</summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/backups").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Backups");

        g.MapGet("", (DatabaseBackups backups, BackupSettingsStore settings) => Status(backups, settings));

        g.MapPut("/settings", (BackupSettingsDto s, DatabaseBackups backups, BackupSettingsStore settings) =>
        {
            if (s.Keep is < 1 or > BackupSettingsDto.MaxKeep || QuietHours.Zone(s.TimeZone) is null) return Results.BadRequest();
            settings.Save(s);
            return Results.Ok(Status(backups, settings));
        });

        g.MapPost("", async (SonNuageDbContext db, JobEngine engine, CancellationToken ct) =>
        {
            var suite = await engine.EnqueueOnceAsync(db, MaintenanceJobs.DatabaseBackup(), ct);
            return Results.Accepted(value: suite?.Id);
        });
    }

    private static BackupStatusDto Status(DatabaseBackups backups, BackupSettingsStore settings) =>
        new(settings.Settings, backups.Folder, backups.List());
}
