using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.Server.Tests;

/// <summary>Empreinte de test : dérivée du contenu du fichier (deux fichiers identiques ont la même).</summary>
public sealed class FakeFingerprinter : IFingerprinter
{
    public bool Available => true;

    public async Task<RawFingerprint> ComputeAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var values = new uint[64];
        for (var i = 0; i < values.Length; i += 8)
        {
            var hash = SHA256.HashData([.. bytes, (byte)i]);
            for (var j = 0; j < 8; j++) values[i + j] = BitConverter.ToUInt32(hash, j * 4);
        }
        return new RawFingerprint(1, values);
    }
}

/// <summary>
/// Trois paires de doublons, chacune avec une copie dans music/ (lecture seule) et une dans uploads/ : deux reconnues
/// par leur identifiant d'enregistrement MusicBrainz, une par son empreinte seulement.
/// </summary>
public sealed class DuplicateLibraryFactory : SonNuageFactory
{
    public static readonly Guid MergedRecording = Guid.Parse("11111111-2222-4333-8444-555555555555");
    public static readonly Guid HiddenRecording = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");

    public string MergedInMusic => Path.Combine(MusicPath, "Merge Artist", "Merge Album", "01 Merge Song.wav");
    public string MergedInUploads => Path.Combine(UploadPath, "Merge Artist", "Merge Album", "01 Merge Song.wav");
    public string HiddenInMusic => Path.Combine(MusicPath, "Hide Artist", "Hide Album", "01 Hide Song.wav");
    public string HiddenInUploads => Path.Combine(UploadPath, "Hide Artist", "Hide Album", "01 Hide Song.wav");
    public static readonly Guid ListRecording = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");

    public string ListInMusic => Path.Combine(MusicPath, "List Artist", "List Album", "01 List Song.wav");
    public string ListInUploads => Path.Combine(UploadPath, "List Artist", "List Album", "01 List Song.wav");
    public string PrintInMusic => Path.Combine(MusicPath, "Print Artist", "Print Album", "01 Print Song.wav");
    public string PrintInUploads => Path.Combine(UploadPath, "Print Artist", "Print Album", "01 Print Song (envoi).wav");

    public DuplicateLibraryFactory()
    {
        JobEngine.RetryDelay = TimeSpan.FromMilliseconds(100);
        JobEngine.PollInterval = TimeSpan.FromMilliseconds(200);
        foreach (var path in new[] { MergedInMusic, MergedInUploads }) Tagged(path, 11025, MergedRecording, "Merge Song", "Merge Artist");
        foreach (var path in new[] { HiddenInMusic, HiddenInUploads }) Tagged(path, 12000, HiddenRecording, "Hide Song", "Hide Artist");
        foreach (var path in new[] { ListInMusic, ListInUploads }) Tagged(path, 13000, ListRecording, "List Song", "List Artist");
        WriteWav(PrintInMusic, seconds: 4);
        WriteWav(PrintInUploads, seconds: 4);
    }

    private static void Tagged(string path, int sampleRate, Guid recording, string title, string artist)
    {
        WriteWav(path, seconds: 2, sampleRate);
        TaggedLibraryFactory.TagWav(path, tag =>
        {
            tag.Title = title;
            tag.Performers = [artist];
            tag.AlbumArtists = [artist];
            tag.MusicBrainzTrackId = recording.ToString();
        });
    }

    protected override void ConfigureMore(IWebHostBuilder builder) =>
        builder.ConfigureServices(s => s.AddSingleton<IFingerprinter, FakeFingerprinter>());
}

public sealed class DuplicateTests(DuplicateLibraryFactory factory) : IClassFixture<DuplicateLibraryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> WithDbAsync<T>(Func<SonNuageDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>());
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        return admin;
    }

    private Task<Guid> TrackIdAsync(string path) =>
        WithDbAsync(db => db.Tracks.Where(t => t.FilePath == Path.GetFullPath(path)).Select(t => t.Id).SingleAsync(Ct));

    private async Task<DuplicatesDto> DuplicatesAsync(HttpClient admin) => (await admin.GetFromJsonAsync<DuplicatesDto>("/api/admin/duplicates", Ct))!;

    [Fact]
    public async Task Duplicates_are_for_admins_only()
    {
        var admin = await AdminAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("carol", "carol-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        var carol = await factory.ClientAsync("carol", "carol-password");
        var anonymous = factory.CreateClient();
        var resolve = new ResolveDuplicatesRequest(Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/duplicates", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.GetAsync("/api/admin/duplicates", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsync("/api/admin/duplicates/fingerprints", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsJsonAsync("/api/admin/duplicates/resolve", resolve, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/duplicates/resolve", resolve, Ct)).StatusCode);
    }

    [Fact]
    public async Task Copies_of_the_same_recording_are_grouped_with_their_file_details()
    {
        var admin = await AdminAsync();
        var listMusic = await TrackIdAsync(factory.ListInMusic);
        var listUploads = await TrackIdAsync(factory.ListInUploads);

        var group = Assert.Single((await DuplicatesAsync(admin)).Groups, g => g.RecordingId == DuplicateLibraryFactory.ListRecording);
        Assert.Equal("recording", group.Reason);
        Assert.Equal(new[] { listMusic, listUploads }.Order(), group.Copies.Select(c => c.TrackId).Order());
        var music = Assert.Single(group.Copies, c => c.TrackId == listMusic);
        var uploads = Assert.Single(group.Copies, c => c.TrackId == listUploads);
        Assert.False(music.InUploads);
        Assert.True(uploads.InUploads);
        Assert.Equal("wav", music.Format);
        Assert.EndsWith("List Artist/List Album/01 List Song.wav", music.RelativePath);
        Assert.True(music.DurationSeconds > 1.5);
        Assert.True(music.FileSize > 0);
        Assert.Equal((0, 0, 0), (music.Plays, music.Favorites, music.Playlists));
        Assert.Contains(group.SuggestedKeepId, new[] { listMusic, listUploads });

        // Les morceaux uniques ne forment pas de groupe.
        var opening = await WithDbAsync(db => db.Tracks.Where(t => t.Title == "Opening").Select(t => t.Id).SingleAsync(Ct));
        Assert.DoesNotContain((await DuplicatesAsync(admin)).Groups, g => g.Copies.Any(c => c.TrackId == opening));
    }

    [Fact]
    public async Task Fingerprints_are_computed_by_a_job_of_the_acoustid_lane_and_find_copies_without_ids()
    {
        var admin = await AdminAsync();
        var before = await DuplicatesAsync(admin);
        Assert.True(before.FingerprintAvailable);
        var printIds = new[] { await TrackIdAsync(factory.PrintInMusic), await TrackIdAsync(factory.PrintInUploads) };

        // File en pause : la tâche attend (comme pendant les heures calmes), puis tourne à la reprise.
        (await admin.PostAsync($"/api/admin/jobs/lanes/{JobLanes.AcoustId}/pause", null, Ct)).EnsureSuccessStatusCode();
        var response = await admin.PostAsync("/api/admin/duplicates/fingerprints", null, Ct);
        response.EnsureSuccessStatusCode();
        var suiteId = (await response.Content.ReadFromJsonAsync<Guid?>(Ct))!.Value;
        await Task.Delay(500, Ct);
        var queued = (await admin.GetFromJsonAsync<PagedResult<JobDto>>($"/api/admin/jobs/suites/{suiteId}", Ct))!.Items;
        Assert.All(queued, j => Assert.Equal(("Queued", JobLanes.AcoustId), (j.Status, j.Lane)));
        (await admin.PostAsync($"/api/admin/jobs/lanes/{JobLanes.AcoustId}/resume", null, Ct)).EnsureSuccessStatusCode();
        await WaitForSuiteAsync(admin, suiteId);

        var after = await DuplicatesAsync(admin);
        Assert.Equal(after.Tracks, after.Fingerprinted);
        var group = Assert.Single(after.Groups, g => g.Copies.Any(c => c.TrackId == printIds[0]));
        Assert.Equal("fingerprint", group.Reason);
        Assert.Equal(printIds.Order(), group.Copies.Select(c => c.TrackId).Order());

        // Tout est à jour : rien de plus à calculer.
        response = await admin.PostAsync("/api/admin/duplicates/fingerprints", null, Ct);
        await WaitForSuiteAsync(admin, (await response.Content.ReadFromJsonAsync<Guid?>(Ct))!.Value);
        Assert.Equal(after.Fingerprinted, (await DuplicatesAsync(admin)).Fingerprinted);
    }

    [Fact]
    public async Task Keeping_one_copy_moves_favorites_ratings_plays_and_playlists_and_deletes_the_uploaded_file()
    {
        var admin = await AdminAsync();
        var kept = await TrackIdAsync(factory.MergedInMusic);
        var removed = await TrackIdAsync(factory.MergedInUploads);
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("dave", "dave-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        var dave = await factory.ClientAsync("dave", "dave-password");

        // L'admin n'a que la copie envoyée ; Dave a les deux en favori et une note sur chacune.
        (await admin.PutAsync($"/api/me/favorites/{removed}", null, Ct)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/me/ratings/{removed}", new RatingRequest(4), Ct)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/me/plays", new PlayEventRequest(removed, 120, true), Ct)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/me/plays", new PlayEventRequest(removed, 30, false), Ct)).EnsureSuccessStatusCode();
        var playlist = (await (await admin.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest("Doublons", null, [removed, kept]), Ct))
            .Content.ReadFromJsonAsync<PlaylistDto>(Ct))!;
        (await dave.PutAsync($"/api/me/favorites/{removed}", null, Ct)).EnsureSuccessStatusCode();
        (await dave.PutAsync($"/api/me/favorites/{kept}", null, Ct)).EnsureSuccessStatusCode();
        (await dave.PutAsJsonAsync($"/api/me/ratings/{kept}", new RatingRequest(2), Ct)).EnsureSuccessStatusCode();
        (await dave.PutAsJsonAsync($"/api/me/ratings/{removed}", new RatingRequest(5), Ct)).EnsureSuccessStatusCode();

        var copy = Assert.Single((await DuplicatesAsync(admin)).Groups, g => g.RecordingId == DuplicateLibraryFactory.MergedRecording)
            .Copies.Single(c => c.TrackId == removed);
        Assert.Equal((2, 2, 1), (copy.Plays, copy.Favorites, copy.Playlists));

        var response = await admin.PostAsJsonAsync("/api/admin/duplicates/resolve", new ResolveDuplicatesRequest(kept, [removed]), Ct);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ResolveDuplicatesResultDto>(Ct))!;
        Assert.Equal(1, result.Removed);
        Assert.EndsWith("Merge Artist/Merge Album/01 Merge Song.wav", Assert.Single(result.Deleted));
        Assert.Empty(result.Hidden);

        Assert.False(File.Exists(factory.MergedInUploads));
        Assert.True(File.Exists(factory.MergedInMusic));
        Assert.False(await WithDbAsync(db => db.Tracks.AnyAsync(t => t.Id == removed, Ct)));

        var adminFavorites = await admin.GetFromJsonAsync<List<TrackDto>>("/api/me/favorites", Ct);
        Assert.Contains(adminFavorites!, t => t.Id == kept);
        Assert.Single((await dave.GetFromJsonAsync<List<TrackDto>>("/api/me/favorites", Ct))!, t => t.Id == kept);
        var (adminId, daveId) = await WithDbAsync(async db => (
            await db.Users.Where(u => u.Username == "admin").Select(u => u.Id).SingleAsync(Ct),
            await db.Users.Where(u => u.Username == "dave").Select(u => u.Id).SingleAsync(Ct)));
        Assert.Equal(4, await WithDbAsync(db => db.Ratings.Where(r => r.UserId == adminId && r.ItemId == kept).Select(r => r.Value).SingleAsync(Ct)));
        // Les deux copies notées : la note la plus récente reste.
        Assert.Equal(5, await WithDbAsync(db => db.Ratings.Where(r => r.UserId == daveId && r.ItemId == kept).Select(r => r.Value).SingleAsync(Ct)));
        Assert.False(await WithDbAsync(db => db.Ratings.AnyAsync(r => r.ItemId == removed, Ct)));
        Assert.Equal(2, await WithDbAsync(db => db.PlayEvents.CountAsync(p => p.TrackId == kept && p.UserId == adminId, Ct)));
        var entries = (await admin.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist.Id}", Ct))!.Entries;
        Assert.Equal([kept, kept], entries.OrderBy(e => e.Position).Select(e => e.Track.Id));

        // Journal : une suite terminée dans le volet Tâches, qui nomme le fichier supprimé.
        var job = Assert.Single((await admin.GetFromJsonAsync<PagedResult<JobDto>>($"/api/admin/jobs/suites/{result.AuditSuiteId}", Ct))!.Items);
        Assert.Equal("Succeeded", job.Status);
        var logs = (await admin.GetFromJsonAsync<List<JobLogDto>>($"/api/admin/jobs/{job.Id}/logs", Ct))!;
        var lines = logs.Select(l => Translations.Instance.Translate("en", l.Message)).ToList();
        Assert.Contains(lines, l => l.StartsWith("File deleted", StringComparison.Ordinal) && l.Contains(Path.GetFullPath(factory.MergedInUploads), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_only_copy_is_hidden_from_the_library_but_never_deleted()
    {
        var admin = await AdminAsync();
        var kept = await TrackIdAsync(factory.HiddenInUploads);
        var hidden = await TrackIdAsync(factory.HiddenInMusic);

        var response = await admin.PostAsJsonAsync("/api/admin/duplicates/resolve", new ResolveDuplicatesRequest(kept, [hidden]), Ct);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ResolveDuplicatesResultDto>(Ct))!;
        Assert.Empty(result.Deleted);
        Assert.EndsWith("Hide Artist/Hide Album/01 Hide Song.wav", Assert.Single(result.Hidden));

        Assert.True(File.Exists(factory.HiddenInMusic));
        Assert.False(await WithDbAsync(db => db.Tracks.AnyAsync(t => t.Id == hidden, Ct)));
        // Un scan complet ne le fait pas revenir.
        await factory.Services.GetRequiredService<ScanCoordinator>().RunFullScanAsync(null, Ct);
        Assert.False(await WithDbAsync(db => db.Tracks.AnyAsync(t => t.FilePath == Path.GetFullPath(factory.HiddenInMusic), Ct)));
        Assert.True(await WithDbAsync(db => db.Tracks.AnyAsync(t => t.Id == kept, Ct)));
    }

    [Fact]
    public async Task Resolving_checks_the_copies()
    {
        var admin = await AdminAsync();
        var opening = await WithDbAsync(db => db.Tracks.Where(t => t.Title == "Opening").Select(t => t.Id).SingleAsync(Ct));
        var encore = await WithDbAsync(db => db.Tracks.Where(t => t.Title == "Encore").Select(t => t.Id).SingleAsync(Ct));

        async Task<HttpStatusCode> ResolveAsync(Guid keep, params Guid[] remove) =>
            (await admin.PostAsJsonAsync("/api/admin/duplicates/resolve", new ResolveDuplicatesRequest(keep, remove), Ct)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await ResolveAsync(opening));
        Assert.Equal(HttpStatusCode.BadRequest, await ResolveAsync(opening, opening));
        Assert.Equal(HttpStatusCode.NotFound, await ResolveAsync(opening, Guid.NewGuid()));
        // Deux morceaux différents : refusé, rien n'est supprimé.
        Assert.Equal(HttpStatusCode.Conflict, await ResolveAsync(opening, encore));
        Assert.True(await WithDbAsync(db => db.Tracks.AnyAsync(t => t.Id == encore, Ct)));
    }

    [Theory]
    [InlineData("uploads/a/b.wav", true)]
    [InlineData("uploads/../music/b.wav", false)]
    [InlineData("uploads2/b.wav", false)]
    [InlineData("music/b.wav", false)]
    [InlineData("uploads", false)]
    public void Only_files_inside_the_upload_folder_count_as_deletable(string relative, bool inside)
    {
        var options = new SonNuageOptions { UploadPath = Path.Combine(Path.GetTempPath(), "sn-guard", "uploads") };
        var path = Path.Combine(Path.GetTempPath(), "sn-guard", relative);
        Assert.Equal(inside, UploadArea.Contains(options, path));
    }

    [Fact]
    public void The_deletion_guard_refuses_any_file_outside_the_upload_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "sn-guard-" + Guid.NewGuid().ToString("N"));
        var options = new SonNuageOptions { UploadPath = Path.Combine(root, "uploads") };
        var outside = Path.Combine(root, "music", "keep.wav");
        var sneaky = Path.Combine(root, "uploads", "..", "music", "keep.wav");
        var inside = Path.Combine(root, "uploads", "drop.wav");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
            File.WriteAllText(outside, "x");
            File.WriteAllText(inside, "x");

            Assert.False(UploadArea.TryDelete(options, outside));
            Assert.False(UploadArea.TryDelete(options, sneaky));
            Assert.True(File.Exists(outside));
            Assert.True(UploadArea.TryDelete(options, inside));
            Assert.False(File.Exists(inside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Close_fingerprints_match_and_different_ones_do_not()
    {
        var random = new Random(42);
        var a = Enumerable.Range(0, 200).Select(_ => (uint)random.NextInt64(0, uint.MaxValue)).ToArray();
        // Même enregistrement, autre encodage : quelques bits changent.
        var b = a.Select((v, i) => i % 4 == 0 ? v ^ 0b1011 : v).ToArray();
        var other = Enumerable.Range(0, 200).Select(_ => (uint)random.NextInt64(0, uint.MaxValue)).ToArray();
        Assert.True(Fingerprints.Similarity(a, b) > 0.95);
        Assert.True(Fingerprints.Similarity(a, other) < 0.7);
        // Décalé d'une trame (délai d'encodeur) : toujours reconnu.
        Assert.True(Fingerprints.Similarity(a, a[1..]) > 0.95);
        Assert.Equal(a, Fingerprints.FromBytes(Fingerprints.ToBytes(a)));
    }

    [Fact]
    public void Groups_join_same_recordings_and_close_fingerprints_of_similar_length()
    {
        var recording = Guid.NewGuid();
        var print = Enumerable.Range(0, 100).Select(i => (uint)(i * 2654435761u)).ToArray();
        var far = print.Select(v => ~v).ToArray();
        DuplicateCandidate C(int id, Guid? rec, double duration, uint[]? fp) => new(new Guid(id, 0, 0, new byte[8]), rec, duration, fp);
        var tracks = new[]
        {
            C(1, recording, 200, null), C(2, recording, 260, null), // même enregistrement, quelle que soit la durée
            C(3, null, 180, print), C(4, null, 181, print), C(5, null, 240, print), // 5 : même empreinte mais une minute de plus
            C(6, null, 180, far), C(7, null, 300, null),
        };

        var groups = DuplicateFinder.Find(tracks).Select(g => (g.Reason, Ids: g.Ids.Select(id => id.ToByteArray()[0]).Order().ToArray())).ToList();
        Assert.Equal(2, groups.Count);
        Assert.Contains(("recording", new byte[] { 1, 2 }), groups.Select(g => (g.Reason, g.Ids)), new GroupComparer());
        Assert.Contains(("fingerprint", new byte[] { 3, 4 }), groups.Select(g => (g.Reason, g.Ids)), new GroupComparer());
    }

    private sealed class GroupComparer : IEqualityComparer<(string, byte[])>
    {
        public bool Equals((string, byte[]) x, (string, byte[]) y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2);
        public int GetHashCode((string, byte[]) obj) => obj.Item1.GetHashCode();
    }

    private static async Task WaitForSuiteAsync(HttpClient admin, Guid suiteId)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var jobs = (await admin.GetFromJsonAsync<PagedResult<JobDto>>($"/api/admin/jobs/suites/{suiteId}", Ct))!.Items;
            if (jobs.Count > 0 && jobs.All(j => j.Status is "Succeeded" or "Failed" or "Canceled"))
            {
                Assert.All(jobs, j => Assert.Equal("Succeeded", j.Status));
                return;
            }
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Empreintes trop longues");
            await Task.Delay(100, Ct);
        }
    }
}
