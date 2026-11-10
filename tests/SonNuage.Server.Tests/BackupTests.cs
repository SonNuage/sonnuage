using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using SonNuage.Contracts;
using SonNuage.Server.Storage;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class BackupTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_backup_is_a_readable_copy_of_the_database()
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsync("/api/admin/backups", null, Ct)).EnsureSuccessStatusCode();

        var watch = Stopwatch.StartNew();
        BackupStatusDto status;
        while ((status = (await admin.GetFromJsonAsync<BackupStatusDto>("/api/admin/backups", Ct))!).Files.Count == 0)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "aucune sauvegarde");
            await Task.Delay(100, Ct);
        }
        var file = Assert.Single(status.Files);
        Assert.Matches(@"^sonnuage-\d{8}-\d{6}\.db$", file.Name);
        Assert.True(file.Bytes > 0);

        await using var copy = new SqliteConnection($"Data Source={Path.Combine(status.Folder, file.Name)};Mode=ReadOnly;Pooling=False");
        await copy.OpenAsync(Ct);
        await using var count = copy.CreateCommand();
        count.CommandText = """SELECT COUNT(*) FROM "Users" WHERE "Username" = 'admin'""";
        Assert.Equal(1L, await count.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Backup_settings_are_checked_and_kept()
    {
        var admin = await factory.AdminClientAsync();
        var s = BackupSettingsDto.Defaults with { TimeZone = "UTC", Keep = 3, At = new TimeOnly(2, 30) };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/backups/settings", s with { Keep = 0 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/backups/settings", s with { TimeZone = "Mars/Olympus" }, Ct)).StatusCode);
        (await admin.PutAsJsonAsync("/api/admin/backups/settings", s, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(s, (await admin.GetFromJsonAsync<BackupStatusDto>("/api/admin/backups", Ct))!.Settings);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/backups", Ct)).StatusCode);
    }

    [Fact]
    public void Only_the_newest_backups_are_kept()
    {
        var dir = Directory.CreateTempSubdirectory("sonnuage-backups-").FullName;
        string[] names = ["sonnuage-20261101-040000.db", "sonnuage-20261102-040000.db", "sonnuage-20261103-040000.db", "sonnuage-20261104-040000.db"];
        foreach (var n in names.Append("autre.db")) File.WriteAllText(Path.Combine(dir, n), "x");

        DatabaseBackups.Prune(dir, keep: 2);

        Assert.Equal(["autre.db", "sonnuage-20261103-040000.db", "sonnuage-20261104-040000.db"],
            Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void The_nightly_slot_follows_its_time_zone()
    {
        var s = BackupSettingsDto.Defaults with { At = new TimeOnly(4, 0), TimeZone = "Europe/Paris" };
        // 4 h à Paris en novembre = 3 h UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 3, 0, 0, TimeSpan.Zero),
            DatabaseBackups.LastSlot(s, new DateTimeOffset(2026, 11, 10, 3, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero),
            DatabaseBackups.LastSlot(s, new DateTimeOffset(2026, 11, 10, 2, 59, 0, TimeSpan.Zero)));
    }
}
