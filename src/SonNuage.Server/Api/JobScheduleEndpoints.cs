using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Api;

/// <summary>Tâches planifiées du volet Tâches : liste, création, modification, suppression et lancement immédiat.</summary>
public static class JobScheduleEndpoints
{
    public static void MapJobScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/jobs/schedules").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Jobs");

        g.MapGet("", async (SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var rows = await db.JobSchedules.AsNoTracking().ToListAsync(ct);
            return rows.OrderBy(s => s.CreatedAt).Select(s => JobSchedules.ToDto(s, now)).ToList();
        });

        g.MapPost("", async (JobScheduleRequest req, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (JobSchedules.Validate(req) is { } error) return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            var now = time.GetUtcNow();
            var s = new JobSchedule { Kind = req.Kind, TimeZone = req.TimeZone, CreatedAt = now };
            JobSchedules.Apply(s, req, now);
            db.JobSchedules.Add(s);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/jobs/schedules/{s.Id}", JobSchedules.ToDto(s, now));
        });

        g.MapPut("/{id:guid}", async (Guid id, JobScheduleRequest req, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (JobSchedules.Validate(req) is { } error) return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            if (await db.JobSchedules.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } s) return Results.NotFound();
            var now = time.GetUtcNow();
            JobSchedules.Apply(s, req, now);
            await db.SaveChangesAsync(ct);
            return Results.Ok(JobSchedules.ToDto(s, now));
        });

        g.MapDelete("/{id:guid}", async (Guid id, SonNuageDbContext db, CancellationToken ct) =>
            await db.JobSchedules.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());

        // Lancer maintenant : la tâche est demandée tout de suite, les passages prévus restent.
        g.MapPost("/{id:guid}/run", async (Guid id, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.JobSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is not { } s) return Results.NotFound();
            var now = time.GetUtcNow();
            await JobSchedules.LaunchAsync(http.RequestServices, db, s, now, ct);
            // Relue : les dates enregistrées sont arrondies par la base.
            return Results.Ok(JobSchedules.ToDto(await db.JobSchedules.AsNoTracking().FirstAsync(x => x.Id == id, ct), now));
        });
    }
}
