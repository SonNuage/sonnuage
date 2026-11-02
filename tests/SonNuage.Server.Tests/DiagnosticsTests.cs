using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Ce qu'une appli joint à un rapport de plantage : ses dernières actions et le contexte de l'appareil.</summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public void Breadcrumbs_keep_the_latest_actions_with_their_time()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 2, 21, 30, 5, 120, TimeSpan.Zero));
        var crumbs = new Breadcrumbs(time);
        for (var i = 0; i < Breadcrumbs.Capacity + 5; i++) crumbs.Add($"action {i}");

        var trail = crumbs.Snapshot();
        Assert.Equal(Breadcrumbs.Capacity, trail.Count);
        Assert.Equal($"21:30:05.120 action 5", trail[0]);
        Assert.EndsWith($"action {Breadcrumbs.Capacity + 4}", trail[^1]);
    }

    [Fact]
    public void A_crash_report_starts_with_the_error_then_context_and_trail()
    {
        Exception crash;
        try { throw new InvalidOperationException("La file est vide"); }
        catch (Exception ex) { crash = ex; }
        var at = new DateTimeOffset(2026, 11, 2, 21, 30, 0, TimeSpan.Zero);
        var device = new CrashDevice("android", "1.4.0 (42)", "Google Pixel 8", "Android 15 (API 35)",
            [("ABI", "arm64-v8a"), ("Mémoire", "120 Mo / 512 Mo")]);

        var report = CrashReports.Create(at, "AppDomain", crash, device, ["21:29:58.000 → album/42", "21:29:59.500 lecture : suivant"]);

        Assert.NotEqual(Guid.Empty, report.Id);
        Assert.Equal(at, report.OccurredAt);
        Assert.Equal(("android", "1.4.0 (42)", "Google Pixel 8", "Android 15 (API 35)", "AppDomain"), (report.App, report.AppVersion, report.Device, report.Os, report.Source));
        var lines = report.Report.Split('\n');
        Assert.Equal("System.InvalidOperationException: La file est vide", lines[0]);
        Assert.Contains(lines, l => l.Contains(nameof(A_crash_report_starts_with_the_error_then_context_and_trail)));
        Assert.Contains("ABI : arm64-v8a", lines);
        Assert.True(Array.IndexOf(lines, "21:29:58.000 → album/42") < Array.IndexOf(lines, "21:29:59.500 lecture : suivant"));
        Assert.True(report.Report.Length <= CrashReportRequest.MaxReportLength);
    }

    [Fact]
    public void An_oversized_report_is_cut_to_what_the_server_accepts()
    {
        var device = new CrashDevice("android", "1", "d", "o", []);
        var report = CrashReports.Create(DateTimeOffset.UtcNow, "s", new Exception(new string('x', CrashReportRequest.MaxReportLength * 2)), device, []);
        Assert.Equal(CrashReportRequest.MaxReportLength, report.Report.Length);
        Assert.StartsWith("System.Exception: xxx", report.Report);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
