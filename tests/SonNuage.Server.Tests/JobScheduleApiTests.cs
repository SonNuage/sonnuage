using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class JobScheduleApiTests(JobsFactory factory) : IClassFixture<JobsFactory>
{
    private const string Url = "/api/admin/jobs/schedules";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobScheduleRequest SundayRescan => new(ScheduledJobKinds.AcoustIdRescan, [0], new TimeOnly(3, 0), "Europe/Paris",
        Options: new JobScheduleOptions(Apply: true, Threshold: 0.92, IncludeIdentified: true));

    private static async Task<JobScheduleDto> CreateAsync(HttpClient admin, JobScheduleRequest request)
    {
        var response = await admin.PostAsJsonAsync(Url, request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobScheduleDto>(Ct))!;
    }

    [Fact]
    public async Task Schedules_are_for_admins_only()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url, Ct)).StatusCode);
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("planner", "planner-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        var user = await factory.ClientAsync("planner", "planner-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync(Url, SundayRescan, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_schedule_is_checked_before_it_is_saved()
    {
        var admin = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, SundayRescan with { Kind = "format-disk" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, SundayRescan with { Days = [] }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, SundayRescan with { Days = [7] }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, SundayRescan with { TimeZone = "Mars/Olympus" }, Ct)).StatusCode);

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Url}/{unknown}", SundayRescan, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Url}/{unknown}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{Url}/{unknown}/run", null, Ct)).StatusCode);

        var saved = await CreateAsync(admin, SundayRescan);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Url}/{saved.Id}", SundayRescan with { Days = [1, 9] }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_schedule_is_created_listed_edited_and_deleted()
    {
        var admin = await factory.AdminClientAsync();
        var created = await CreateAsync(admin, SundayRescan);
        Assert.Equal(ScheduledJobKinds.AcoustIdRescan, created.Kind);
        Assert.Equal([0], created.Days);
        Assert.Equal(new JobScheduleOptions(true, 0.92, true), created.Options);
        Assert.True(created.Enabled);
        Assert.Null(created.LastRunAt);
        var paris = QuietHours.Zone("Europe/Paris")!;
        Assert.Equal(JobScheduling.NextSlot(new ScheduleTimes([DayOfWeek.Sunday], new TimeOnly(3, 0), paris), DateTimeOffset.UtcNow), created.NextRunAt);
        Assert.Contains(await admin.GetFromJsonAsync<List<JobScheduleDto>>(Url, Ct) ?? [], s => s.Id == created.Id);

        // Désactivée, et passée à un autre type : plus de prochain passage, et ses options d'avant ne la suivent pas.
        var edited = await admin.PutAsJsonAsync($"{Url}/{created.Id}",
            SundayRescan with { Kind = ScheduledJobKinds.DatabaseBackup, Days = [1, 4], At = new TimeOnly(22, 30), Enabled = false }, Ct);
        edited.EnsureSuccessStatusCode();
        var dto = (await edited.Content.ReadFromJsonAsync<JobScheduleDto>(Ct))!;
        Assert.Equal((ScheduledJobKinds.DatabaseBackup, new TimeOnly(22, 30), false), (dto.Kind, dto.At, dto.Enabled));
        Assert.Equal([1, 4], dto.Days);
        Assert.Null(dto.Options);
        Assert.Null(dto.NextRunAt);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Url}/{created.Id}", Ct)).StatusCode);
        Assert.DoesNotContain(await admin.GetFromJsonAsync<List<JobScheduleDto>>(Url, Ct) ?? [], s => s.Id == created.Id);
    }

    [Fact]
    public async Task Run_now_requests_the_task_and_remembers_it()
    {
        var admin = await factory.AdminClientAsync();
        var schedule = await CreateAsync(admin, new JobScheduleRequest(ScheduledJobKinds.TranscodePurge, [3], new TimeOnly(4, 0), "UTC"));

        var response = await admin.PostAsync($"{Url}/{schedule.Id}/run", null, Ct);
        response.EnsureSuccessStatusCode();
        var ran = (await response.Content.ReadFromJsonAsync<JobScheduleDto>(Ct))!;

        Assert.NotNull(ran.LastRunAt);
        var suiteId = Assert.IsType<Guid>(ran.LastSuiteId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        Assert.Equal(JobSuiteKinds.TranscodePurge, await db.JobSuites.Where(s => s.Id == suiteId).Select(s => s.Kind).SingleAsync(Ct));
        Assert.Equal(ran.LastRunAt, (await admin.GetFromJsonAsync<List<JobScheduleDto>>(Url, Ct))!.Single(s => s.Id == schedule.Id).LastRunAt);
    }
}

public sealed class JobSchedulerTests(JobsFactory factory) : IClassFixture<JobsFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slot_missed_while_the_server_was_off_runs_once_on_return()
    {
        await factory.AdminClientAsync();
        var now = DateTimeOffset.UtcNow;
        var everyDay = Enumerable.Range(0, 7).Aggregate(0, (mask, d) => mask | 1 << d);
        // Chaque jour une heure plus tôt : le passage d'aujourd'hui (et les neuf d'avant) ont été manqués.
        var missed = new JobSchedule
        {
            Kind = ScheduledJobKinds.TranscodePurge, Days = everyDay, At = TimeOnly.FromDateTime(now.AddHours(-1).UtcDateTime), TimeZone = "UTC",
            CreatedAt = now.AddDays(-20), UpdatedAt = now.AddDays(-20), LastRunAt = now.AddDays(-10),
        };
        var disabled = new JobSchedule
        {
            Kind = ScheduledJobKinds.DatabaseBackup, Days = everyDay, At = missed.At, TimeZone = "UTC", Enabled = false,
            CreatedAt = now.AddDays(-20), UpdatedAt = now.AddDays(-20),
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            db.JobSchedules.AddRange(missed, disabled);
            await db.SaveChangesAsync(Ct);
        }

        var scheduler = factory.Services.GetRequiredService<JobScheduler>();
        await scheduler.RunDueAsync(Ct);
        await scheduler.RunDueAsync(Ct);

        using var check = factory.Services.CreateScope();
        var rows = check.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var ran = await rows.JobSchedules.AsNoTracking().SingleAsync(s => s.Id == missed.Id, Ct);
        Assert.True(ran.LastRunAt >= now);
        Assert.NotNull(ran.LastSuiteId);
        Assert.Equal(1, await rows.JobSuites.CountAsync(s => s.Kind == JobSuiteKinds.TranscodePurge, Ct));
        Assert.Null((await rows.JobSchedules.AsNoTracking().SingleAsync(s => s.Id == disabled.Id, Ct)).LastRunAt);
    }
}
