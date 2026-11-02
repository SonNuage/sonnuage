using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

/// <summary>Rapports de plantage : tout utilisateur connecté en envoie, seul l'administrateur les lit.</summary>
public static class CrashReportEndpoints
{
    public static void MapCrashReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/crash-reports", ReceiveAsync).RequireAuthorization().WithTags("Crash reports");

        var admin = app.MapGroup("/api/admin/crash-reports").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Crash reports");
        admin.MapGet("", async (SonNuageDbContext db, CancellationToken ct) =>
            await db.CrashReports.OrderByDescending(r => r.ReceivedAt).Take(500)
                .Select(r => new CrashReportSummaryDto(r.Id, r.OccurredAt, r.ReceivedAt, r.User.Username, r.App, r.AppVersion, r.Device, r.Headline))
                .ToListAsync(ct));
        admin.MapGet("/{id:guid}", async (Guid id, SonNuageDbContext db, CancellationToken ct) =>
            await db.CrashReports.Where(r => r.Id == id)
                .Select(r => new CrashReportDto(r.Id, r.OccurredAt, r.ReceivedAt, r.User.Username, r.App, r.AppVersion, r.Device, r.Os, r.Source, r.Report))
                .FirstOrDefaultAsync(ct) is { } report ? Results.Ok(report) : Results.NotFound());
        admin.MapDelete("/{id:guid}", async (Guid id, SonNuageDbContext db, CancellationToken ct) =>
            await db.CrashReports.Where(r => r.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent());
    }

    private static async Task<IResult> ReceiveAsync(CrashReportRequest req, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Parse(req, http.User.UserId(), time.GetUtcNow()) is not { } report) return Results.BadRequest();
        if (await db.CrashReports.AnyAsync(r => r.Id == report.Id, ct)) return Results.Accepted();

        var since = report.ReceivedAt.AddHours(-1);
        if (await db.CrashReports.CountAsync(r => r.UserId == report.UserId && r.ReceivedAt > since, ct) >= CrashReportRequest.MaxPerHour)
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        db.CrashReports.Add(report);
        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    /// <summary>Valide le rapport reçu : null s'il est vide ou trop gros ; les champs courts sont tronqués.</summary>
    internal static CrashReport? Parse(CrashReportRequest req, Guid userId, DateTimeOffset now)
    {
        if (req.Id == Guid.Empty || string.IsNullOrWhiteSpace(req.Report) || req.Report.Length > CrashReportRequest.MaxReportLength) return null;
        return new CrashReport
        {
            Id = req.Id, UserId = userId, ReceivedAt = now,
            // Une horloge d'appareil fausse ne doit pas placer le rapport dans le futur.
            OccurredAt = req.OccurredAt > now ? now : req.OccurredAt,
            App = Clip(req.App, CrashReport.MaxFieldLength), AppVersion = Clip(req.AppVersion, CrashReport.MaxFieldLength),
            Device = Clip(req.Device, CrashReport.MaxFieldLength), Os = Clip(req.Os, CrashReport.MaxFieldLength),
            Source = Clip(req.Source, CrashReport.MaxFieldLength),
            Headline = Clip(req.Report.TrimStart().Split('\n', 2)[0].TrimEnd('\r'), CrashReport.MaxHeadlineLength),
            Report = req.Report,
        };
    }

    private static string Clip(string? value, int max)
    {
        var v = (value ?? "").Trim();
        return v.Length > max ? v[..max] : v;
    }
}
