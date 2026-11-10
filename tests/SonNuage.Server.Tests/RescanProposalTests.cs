using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.Server.Tests;

/// <summary>Un morceau mal tagué dans uploads/ (réinscriptible) et un autre dans music/ (lecture seule).</summary>
public sealed class ProposalLibraryFactory : SonNuageFactory
{
    public string UploadedFile => Path.Combine(UploadPath, "Mauvais Artiste", "Mauvais Album", "01 Mauvais Titre.wav");

    public ProposalLibraryFactory() => WriteWav(UploadedFile, seconds: 1);
}

public sealed class RescanProposalTests(ProposalLibraryFactory factory) : IClassFixture<ProposalLibraryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Recording = Guid.Parse("0c8b2f6e-1d2a-4c3b-9e4f-5a6b7c8d9e0f");
    private static readonly Guid Release = Guid.Parse("7a6b5c4d-3e2f-4a1b-8c9d-0e1f2a3b4c5d");

    private static MetadataCandidateDto Candidate(string title = "Bon Titre", double score = 0.7, string recording = "0c8b2f6e-1d2a-4c3b-9e4f-5a6b7c8d9e0f") =>
        new(title, "Bon Artiste", "Mauvais Album", "Mauvais Artiste", 1, 1, 2020, null, score, "acoustid", recording, Release.ToString());

    private async Task<T> WithDbAsync<T>(Func<SonNuageDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>());
    }

    /// <summary>Remet le morceau envoyé dans son état d'origine et oublie les propositions (les tests partagent la base).</summary>
    private async Task<(HttpClient Admin, Track Track, Guid SuiteId)> ArrangeAsync(bool locked = false)
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var path = Path.GetFullPath(factory.UploadedFile);
        return await WithDbAsync(async db =>
        {
            await db.RescanProposals.ExecuteDeleteAsync(Ct);
            var track = await db.Tracks.SingleAsync(t => t.FilePath == path, Ct);
            track.Title = "Mauvais Titre";
            track.ArtistName = "Mauvais Artiste";
            track.MusicBrainzRecordingId = null;
            track.MusicBrainzReleaseId = null;
            track.Year = null;
            track.MetadataLocked = locked;
            var suite = new JobSuite { Kind = JobSuiteKinds.AcoustIdRescan, Title = "Rescan de test" };
            db.JobSuites.Add(suite);
            await db.SaveChangesAsync(Ct);
            return (admin, track, suite.Id);
        });
    }

    /// <summary>Ce que fait la tâche de comparaison une fois le meilleur résultat MusicBrainz trouvé.</summary>
    private async Task<string> ConcludeAsync(Guid trackId, MetadataCandidateDto best, bool apply, Guid suiteId, double threshold = 0.85) =>
        await WithDbAsync(async db =>
        {
            var review = factory.Services.GetRequiredService<RescanReview>();
            var track = await db.Tracks.SingleAsync(t => t.Id == trackId, Ct);
            return (await review.ConcludeAsync(db, track, best, apply, threshold, suiteId, Ct)).Result;
        });

    private async Task<PagedResult<RescanProposalDto>> PendingAsync(HttpClient admin, string query = "") =>
        (await admin.GetFromJsonAsync<PagedResult<RescanProposalDto>>($"/api/admin/proposals?status=pending{query}", Ct))!;

    private static Track LibraryTrack() => new()
    {
        FilePath = "/music/a.flac", Title = "Song", ArtistName = "Artist", AlbumArtistName = "Artist", AlbumTitle = "Album", Format = "flac",
    };

    [Fact]
    public void A_rejected_result_is_neither_applied_nor_proposed_again()
    {
        var track = LibraryTrack();
        var outcome = LibraryMatchJob.Evaluate(track, Candidate(score: 0.99), apply: true, threshold: 0.85, rejected: true);
        Assert.Equal("rejected", outcome.Result);
        outcome.Apply(track);
        Assert.Null(track.MusicBrainzRecordingId);
        Assert.Equal("Song", track.Title);
    }

    [Fact]
    public void The_signature_identifies_the_proposed_result_whatever_its_score()
    {
        Assert.Equal(RescanReview.Signature(Candidate(score: 0.6)), RescanReview.Signature(Candidate(score: 0.9)));
        Assert.Equal(RescanReview.Signature(Candidate()), RescanReview.Signature(Candidate(title: " bon titre ")));
        Assert.NotEqual(RescanReview.Signature(Candidate()), RescanReview.Signature(Candidate(title: "Autre Titre")));
        Assert.NotEqual(RescanReview.Signature(Candidate()), RescanReview.Signature(Candidate(recording: Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task Proposals_are_for_admins_only()
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("bob", "bob-password", Roles.User), Ct)).EnsureSuccessStatusCode();
        var bob = await factory.ClientAsync("bob", "bob-password");
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/proposals", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/proposals", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/proposals/suites", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/api/admin/proposals/accept", new DecideProposalsRequest([Guid.NewGuid()]), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/api/admin/proposals/reject", new DecideProposalsRequest([Guid.NewGuid()]), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_rescan_without_apply_keeps_its_proposal_for_review()
    {
        var (admin, track, suiteId) = await ArrangeAsync();

        Assert.Equal("proposed", await ConcludeAsync(track.Id, Candidate(score: 0.92), apply: false, suiteId));

        var page = await PendingAsync(admin);
        var proposal = Assert.Single(page.Items);
        Assert.Equal(track.Id, proposal.TrackId);
        Assert.Equal(suiteId, proposal.SuiteId);
        Assert.Equal("Rescan de test", proposal.SuiteTitle);
        Assert.Equal(0.92, proposal.Score, 3);
        Assert.Equal("Mauvais Titre", proposal.Title);
        Assert.Equal("Bon Titre", proposal.ProposedTitle);
        Assert.Equal("Bon Artiste", proposal.ProposedArtist);
        Assert.Equal(Recording, proposal.RecordingId);
        Assert.Contains(proposal.Changes, c => c is { Field: "title", From: "Mauvais Titre", To: "Bon Titre" });
        Assert.Contains(proposal.Changes, c => c is { Field: "recordingId", From: null } && c.To == Recording.ToString());
        Assert.True(proposal.Writable);

        // Filtres : rescan, plage de score, artiste (actuel ou proposé).
        Assert.Single((await PendingAsync(admin, $"&suiteId={suiteId}")).Items);
        Assert.Empty((await PendingAsync(admin, $"&suiteId={Guid.NewGuid()}")).Items);
        Assert.Single((await PendingAsync(admin, "&minScore=0.9&maxScore=0.95")).Items);
        Assert.Empty((await PendingAsync(admin, "&minScore=0.95")).Items);
        Assert.Single((await PendingAsync(admin, "&artist=mauvais")).Items);
        Assert.Single((await PendingAsync(admin, "&artist=Bon Artiste")).Items);
        Assert.Empty((await PendingAsync(admin, "&artist=personne")).Items);

        var ids = await admin.GetFromJsonAsync<List<Guid>>($"/api/admin/proposals/ids?status=pending&suiteId={suiteId}", Ct);
        Assert.Equal([proposal.Id], ids);
        var suites = await admin.GetFromJsonAsync<List<ProposalSuiteDto>>("/api/admin/proposals/suites", Ct);
        Assert.Contains(suites!, s => s.SuiteId == suiteId && s.Pending == 1);
    }

    [Fact]
    public async Task A_newer_result_replaces_the_pending_proposal_and_the_same_result_is_kept_once()
    {
        var (admin, track, suiteId) = await ArrangeAsync();

        await ConcludeAsync(track.Id, Candidate(score: 0.6), apply: false, suiteId);
        await ConcludeAsync(track.Id, Candidate(score: 0.65), apply: false, suiteId);
        Assert.Equal(0.65, Assert.Single((await PendingAsync(admin)).Items).Score, 3);

        await ConcludeAsync(track.Id, Candidate(title: "Autre Titre", score: 0.7), apply: true, suiteId);
        Assert.Equal("Autre Titre", Assert.Single((await PendingAsync(admin)).Items).ProposedTitle);
        Assert.Equal(1, await WithDbAsync(db => db.RescanProposals.CountAsync(p => p.Status == RescanProposalStatus.Superseded, Ct)));
    }

    [Fact]
    public async Task Accepting_applies_the_correction_the_same_way_as_the_automatic_rescan()
    {
        var (admin, track, suiteId) = await ArrangeAsync();
        await ConcludeAsync(track.Id, Candidate(score: 0.7), apply: true, suiteId);
        var proposal = Assert.Single((await PendingAsync(admin)).Items);

        var response = await admin.PostAsJsonAsync("/api/admin/proposals/accept", new DecideProposalsRequest([proposal.Id]), Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal(new DecideProposalsResultDto(1, 0), await response.Content.ReadFromJsonAsync<DecideProposalsResultDto>(Ct));

        var updated = await WithDbAsync(db => db.Tracks.AsNoTracking().SingleAsync(t => t.Id == track.Id, Ct));
        Assert.Equal("Bon Titre", updated.Title);
        Assert.Equal("Bon Artiste", updated.ArtistName);
        Assert.Equal(Recording, updated.MusicBrainzRecordingId);
        Assert.Equal(Release, updated.MusicBrainzReleaseId);
        Assert.Equal(2020, updated.Year);
        // Correction acceptée : pas une modification à la main, le morceau n'est pas verrouillé.
        Assert.False(updated.MetadataLocked);
        using (var file = TagLib.File.Create(updated.FilePath))
        {
            Assert.Equal("Bon Titre", file.Tag.Title);
            Assert.Equal(["Bon Artiste"], file.Tag.Performers);
            Assert.Equal(Recording.ToString(), file.Tag.MusicBrainzTrackId);
        }

        Assert.Empty((await PendingAsync(admin)).Items);
        var accepted = Assert.Single((await admin.GetFromJsonAsync<PagedResult<RescanProposalDto>>("/api/admin/proposals?status=accepted", Ct))!.Items);
        Assert.Equal("Accepted", accepted.Status);
        Assert.Equal("admin", accepted.DecidedBy);

        // Déjà tranchée : rien de plus.
        response = await admin.PostAsJsonAsync("/api/admin/proposals/accept", new DecideProposalsRequest([proposal.Id]), Ct);
        Assert.Equal(new DecideProposalsResultDto(0, 0), await response.Content.ReadFromJsonAsync<DecideProposalsResultDto>(Ct));
    }

    [Fact]
    public async Task A_track_edited_by_hand_since_the_rescan_is_left_alone()
    {
        var (admin, track, suiteId) = await ArrangeAsync();
        await ConcludeAsync(track.Id, Candidate(), apply: false, suiteId);
        var proposal = Assert.Single((await PendingAsync(admin)).Items);
        await WithDbAsync(db => db.Tracks.Where(t => t.Id == track.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.MetadataLocked, true), Ct));

        var response = await admin.PostAsJsonAsync("/api/admin/proposals/accept", new DecideProposalsRequest([proposal.Id]), Ct);
        Assert.Equal(new DecideProposalsResultDto(0, 1), await response.Content.ReadFromJsonAsync<DecideProposalsResultDto>(Ct));
        Assert.Equal("Mauvais Titre", await WithDbAsync(db => db.Tracks.Where(t => t.Id == track.Id).Select(t => t.Title).SingleAsync(Ct)));
        Assert.Equal(RescanProposalStatus.Superseded, await WithDbAsync(db => db.RescanProposals.Where(p => p.Id == proposal.Id).Select(p => p.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_rejected_proposal_is_remembered_by_later_rescans()
    {
        var (admin, track, suiteId) = await ArrangeAsync();
        await ConcludeAsync(track.Id, Candidate(score: 0.7), apply: true, suiteId);
        var proposal = Assert.Single((await PendingAsync(admin)).Items);

        var response = await admin.PostAsJsonAsync("/api/admin/proposals/reject", new DecideProposalsRequest([proposal.Id]), Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal(new DecideProposalsResultDto(1, 0), await response.Content.ReadFromJsonAsync<DecideProposalsResultDto>(Ct));
        Assert.Empty((await PendingAsync(admin)).Items);

        // Même résultat, même avec un score au-dessus du seuil : ni proposé, ni appliqué.
        Assert.Equal("rejected", await ConcludeAsync(track.Id, Candidate(score: 0.99), apply: true, suiteId));
        Assert.Empty((await PendingAsync(admin)).Items);
        Assert.Equal("Mauvais Titre", await WithDbAsync(db => db.Tracks.Where(t => t.Id == track.Id).Select(t => t.Title).SingleAsync(Ct)));

        // Un autre résultat reste proposé.
        Assert.Equal("low", await ConcludeAsync(track.Id, Candidate(title: "Autre Titre", score: 0.6), apply: true, suiteId));
        Assert.Single((await PendingAsync(admin)).Items);
    }

    [Fact]
    public async Task Decisions_need_a_reasonable_list_of_ids()
    {
        var admin = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/proposals/accept", new DecideProposalsRequest([]), Ct)).StatusCode);
        var tooMany = Enumerable.Range(0, RescanReview.MaxBatch + 1).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/proposals/reject", new DecideProposalsRequest(tooMany), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/proposals?status=nope", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/proposals?minScore=2", Ct)).StatusCode);

        var unknown = await admin.PostAsJsonAsync("/api/admin/proposals/accept", new DecideProposalsRequest([Guid.NewGuid()]), Ct);
        Assert.Equal(new DecideProposalsResultDto(0, 0), await unknown.Content.ReadFromJsonAsync<DecideProposalsResultDto>(Ct));
    }
}
