using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Une appli plantée envoie son rapport ; seul l'administrateur le relit.</summary>
public sealed class CrashReportTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CrashReportRequest Report(Guid? id = null, string report = "System.NullReferenceException: boom\n   at SonNuage.Mobile.Player.Next()") =>
        new(id ?? Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "android", "1.4.0 (42)", "Google Pixel 8", "Android 15 (API 35)", "AppDomain", report);

    private async Task<HttpClient> UserAsync(string name)
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        return await factory.ClientAsync(name, $"{name}-password");
    }

    [Fact]
    public async Task A_user_sends_a_report_and_the_admin_reads_it_back()
    {
        var bob = await UserAsync("crash-bob");
        var sent = Report();
        Assert.Equal(HttpStatusCode.Accepted, (await bob.PostAsJsonAsync("/api/crash-reports", sent, Ct)).StatusCode);

        var admin = await factory.AdminClientAsync();
        var list = await admin.GetFromJsonAsync<List<CrashReportSummaryDto>>("/api/admin/crash-reports", Ct);
        var summary = Assert.Single(list!, r => r.Id == sent.Id);
        Assert.Equal("crash-bob", summary.User);
        Assert.Equal("System.NullReferenceException: boom", summary.Headline);
        Assert.Equal("1.4.0 (42)", summary.AppVersion);

        var detail = await admin.GetFromJsonAsync<CrashReportDto>($"/api/admin/crash-reports/{sent.Id}", Ct);
        Assert.Equal(sent.Report, detail!.Report);
        Assert.Equal("Google Pixel 8", detail.Device);
        Assert.Equal("Android 15 (API 35)", detail.Os);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/crash-reports/{sent.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/crash-reports/{sent.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Sending_the_same_report_twice_stores_it_once()
    {
        var carol = await UserAsync("crash-carol");
        var sent = Report();
        (await carol.PostAsJsonAsync("/api/crash-reports", sent, Ct)).EnsureSuccessStatusCode();
        (await carol.PostAsJsonAsync("/api/crash-reports", sent, Ct)).EnsureSuccessStatusCode();

        var admin = await factory.AdminClientAsync();
        var list = await admin.GetFromJsonAsync<List<CrashReportSummaryDto>>("/api/admin/crash-reports", Ct);
        Assert.Single(list!, r => r.Id == sent.Id);
    }

    [Fact]
    public async Task Reports_are_bounded_and_private()
    {
        var dave = await UserAsync("crash-dave");
        var huge = Report(report: new string('x', CrashReportRequest.MaxReportLength + 1));
        Assert.Equal(HttpStatusCode.BadRequest, (await dave.PostAsJsonAsync("/api/crash-reports", huge, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dave.PostAsJsonAsync("/api/crash-reports", Report(report: " "), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dave.GetAsync("/api/admin/crash-reports", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/crash-reports", Report(), Ct)).StatusCode);

        for (var i = 0; i < CrashReportRequest.MaxPerHour; i++)
            (await dave.PostAsJsonAsync("/api/crash-reports", Report(), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await dave.PostAsJsonAsync("/api/crash-reports", Report(), Ct)).StatusCode);
    }
}
