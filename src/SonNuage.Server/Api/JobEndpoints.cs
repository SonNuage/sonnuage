using System.Linq.Expressions;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Youtube;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Api;

/// <summary>Volet « Tâches » de l'admin : files, suites, tâches, journaux en direct, création et annulation.</summary>
public static class JobEndpoints
{
    public const string AcoustIdRescan = "acoustid-rescan";

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/jobs").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Jobs");

        // status : active (en cours ou en attente), failed, finished, ou rien pour tout.
        g.MapGet("", async (string? status, string? kind, string? q, int? offset, int? limit, SonNuageDbContext db, JobEngine engine, CancellationToken ct) =>
        {
            var lanes = await db.Jobs.Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued)
                .GroupBy(j => new { j.Lane, j.Status }).Select(x => new { x.Key.Lane, x.Key.Status, Count = x.Count() }).ToListAsync(ct);
            var laneDtos = JobLanes.All.Select(l => new JobLaneDto(l, engine.Capacity(l),
                lanes.Where(x => x.Lane == l && x.Status == JobStatus.Running).Sum(x => x.Count),
                lanes.Where(x => x.Lane == l && x.Status == JobStatus.Queued).Sum(x => x.Count))).ToList();

            var query = db.JobSuites.Select(s => new SuiteRow
            {
                Suite = s,
                Total = s.Jobs.Count(),
                Waiting = s.Jobs.Count(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Queued),
                Running = s.Jobs.Count(j => j.Status == JobStatus.Running),
                Succeeded = s.Jobs.Count(j => j.Status == JobStatus.Succeeded),
                Failed = s.Jobs.Count(j => j.Status == JobStatus.Failed),
                Canceled = s.Jobs.Count(j => j.Status == JobStatus.Canceled),
                FinishedAt = s.Jobs.Max(j => j.FinishedAt),
            });
            query = status switch
            {
                "active" => query.Where(r => r.Running > 0 || r.Waiting > 0),
                "failed" => query.Where(r => r.Running == 0 && r.Waiting == 0 && r.Failed > 0),
                "finished" => query.Where(r => r.Running == 0 && r.Waiting == 0),
                _ => query,
            };
            if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(r => r.Suite.Kind == kind);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(r => EF.Functions.Like(r.Suite.Title, $"%{q.Trim()}%"));

            var o = Math.Max(0, offset ?? 0);
            var l = Math.Clamp(limit ?? 50, 1, 200);
            var total = await query.CountAsync(ct);
            // En cours d'abord, puis en attente (la plus ancienne en tête, comme la file), puis les plus récentes.
            var rows = await query
                .OrderBy(r => r.Running > 0 ? 0 : r.Waiting > 0 ? 1 : 2)
                .ThenBy(r => r.Running > 0 || r.Waiting > 0 ? r.Suite.CreatedAt : DateTimeOffset.MinValue)
                .ThenByDescending(r => r.Suite.CreatedAt)
                .Skip(o).Take(l).ToListAsync(ct);
            return new JobsOverviewDto(laneDtos, new PagedResult<JobSuiteDto>(rows.Select(ToDto).ToList(), total, o, l));
        });

        g.MapGet("/suites/{id:guid}", async (Guid id, int? offset, int? limit, SonNuageDbContext db, CancellationToken ct) =>
        {
            var o = Math.Max(0, offset ?? 0);
            var l = Math.Clamp(limit ?? 100, 1, 500);
            var jobs = db.Jobs.Where(j => j.SuiteId == id);
            var total = await jobs.CountAsync(ct);
            var rows = await jobs
                .OrderBy(j => j.Status == JobStatus.Running ? 0 : j.Status == JobStatus.Failed ? 1 : 2)
                .ThenBy(j => j.CreatedAt)
                .Skip(o).Take(l).Select(AsDto).ToListAsync(ct);
            return new PagedResult<JobDto>(rows, total, o, l);
        });

        g.MapGet("/{id:guid}", async (Guid id, SonNuageDbContext db, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            return job is null ? Results.NotFound() : Results.Ok(new JobDetailDto(AsDto.Compile()(job), job.Payload, job.Output));
        });

        // Journal en direct : l'interface redemande les lignes après la dernière reçue.
        g.MapGet("/{id:guid}/logs", async (Guid id, long? after, SonNuageDbContext db, CancellationToken ct) =>
            await db.JobLogs.Where(x => x.JobId == id && x.Id > (after ?? 0)).OrderBy(x => x.Id).Take(500)
                .Select(x => new JobLogDto(x.Id, x.At, x.Level, x.Message)).ToListAsync(ct));

        g.MapPost("", async (CreateJobRequest req, HttpContext http, SonNuageDbContext db, JobEngine engine, YoutubeSettingsStore settings, YoutubeTools tools, CancellationToken ct) =>
        {
            if (req.Kind != AcoustIdRescan) return Results.Problem(Tx.Key("errors.jobs.unknownKind"), statusCode: 400);
            if (string.IsNullOrWhiteSpace(settings.Settings.AcoustIdKey)) return Results.Problem(Tx.Key("errors.jobs.noAcoustIdKey"), statusCode: 409);
            if (tools.Fpcalc is null) return Results.Problem(Tx.Key("errors.jobs.noFpcalc"), statusCode: 409);
            string title;
            if (req.ArtistId is { } artistId)
            {
                var name = await db.Artists.Where(a => a.Id == artistId).Select(a => a.Name).FirstOrDefaultAsync(ct);
                if (name is null) return Results.Problem(Tx.Key("errors.jobs.unknownArtist"), statusCode: 404);
                title = Tx.Key("jobs.suites.rescanArtist", ("artist", name));
            }
            else
            {
                title = Tx.Key("jobs.suites.rescanAll");
            }
            var suite = await engine.EnqueueAsync(db, AcoustIdRescanJob.Plan(title, req.ArtistId, req.Apply, req.Threshold is { } t ? Math.Clamp(t, 0.5, 1) : null, http.User.UserId(), req.IncludeIdentified), ct);
            return Results.Ok(suite.Id);
        });

        g.MapPost("/{id:guid}/cancel", async (Guid id, SonNuageDbContext db, JobEngine engine, CancellationToken ct) =>
            await engine.CancelAsync(db, id, ct) ? Results.NoContent() : Results.Problem(Tx.Key("errors.jobs.notCancelable"), statusCode: 409));

        g.MapPost("/{id:guid}/retry", async (Guid id, SonNuageDbContext db, JobEngine engine, CancellationToken ct) =>
            await engine.RetryAsync(db, id, ct) ? Results.NoContent() : Results.Problem(Tx.Key("errors.jobs.notRetryable"), statusCode: 409));

        g.MapPost("/suites/{id:guid}/cancel", async (Guid id, SonNuageDbContext db, JobEngine engine, CancellationToken ct) =>
        {
            await engine.CancelSuiteAsync(db, id, ct);
            return Results.NoContent();
        });

        g.MapDelete("/suites/{id:guid}", async (Guid id, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (await db.Jobs.AnyAsync(j => j.SuiteId == id && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
                return Results.Problem(Tx.Key("errors.jobs.suiteActive"), statusCode: 409);
            return await db.JobSuites.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private sealed class SuiteRow
    {
        public required JobSuite Suite { get; init; }
        public int Total { get; init; }
        public int Waiting { get; init; }
        public int Running { get; init; }
        public int Succeeded { get; init; }
        public int Failed { get; init; }
        public int Canceled { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
    }

    private static JobSuiteDto ToDto(SuiteRow r)
    {
        var status = r.Running > 0 ? "Running" : r.Waiting > 0 ? "Queued" : r.Failed > 0 ? "Failed" : r.Canceled > 0 ? "Canceled" : "Succeeded";
        return new JobSuiteDto(r.Suite.Id, r.Suite.Kind, r.Suite.Title, status, r.Total, r.Waiting, r.Running, r.Succeeded, r.Failed, r.Canceled,
            r.Suite.CreatedAt, r.Running > 0 || r.Waiting > 0 ? null : r.FinishedAt);
    }

    private static readonly Expression<Func<Job, JobDto>> AsDto = j => new JobDto(j.Id, j.SuiteId, j.DependsOnId, j.Kind, j.Lane, j.Status.ToString(),
        j.Title, j.Error, j.Attempts, j.MaxAttempts, j.Progress, j.CreatedAt, j.StartedAt, j.FinishedAt, j.NotBefore);
}
