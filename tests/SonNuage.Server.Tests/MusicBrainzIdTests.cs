using System.Net;
using System.Net.Http.Json;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.Server.Tests;

/// <summary>Bibliothèque de test avec un morceau tagué par Picard (identifiants MusicBrainz dans l'ID3v2 du WAV).</summary>
public sealed class TaggedLibraryFactory : SonNuageFactory
{
    public static readonly Guid Recording = Guid.Parse("8f3471b5-7e6a-48da-86a9-c1c07a0f47ae");
    public static readonly Guid Release = Guid.Parse("1f4d3c3e-2a5b-4c6d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid ReleaseGroup = Guid.Parse("2a5e9c1d-3b4f-4a6e-8d7c-1b2a3c4d5e6f");
    public static readonly Guid ArtistMbid = Guid.Parse("5b11f4ce-a62d-471e-81fc-a69a8278c7da");

    public TaggedLibraryFactory()
    {
        var path = Path.Combine(MusicPath, "Tagged Artist", "Tagged Album", "01 Song.wav");
        WriteWav(path, seconds: 1);
        TagWav(path, tag =>
        {
            tag.Title = "Tagged Song";
            tag.Performers = ["Tagged Artist"];
            tag.AlbumArtists = ["Tagged Artist"];
            tag.Album = "Tagged Album";
            tag.MusicBrainzTrackId = Recording.ToString();
            tag.MusicBrainzReleaseId = Release.ToString();
            tag.MusicBrainzReleaseGroupId = ReleaseGroup.ToString();
            tag.MusicBrainzArtistId = ArtistMbid.ToString();
            tag.MusicBrainzReleaseArtistId = ArtistMbid.ToString();
        });
    }

    public static void TagWav(string path, Action<TagLib.Tag> edit)
    {
        using var file = TagLib.File.Create(path);
        edit(file.GetTag(TagLib.TagTypes.Id3v2, create: true));
        file.Save();
    }
}

public sealed class MusicBrainzIdTests(TaggedLibraryFactory factory) : IClassFixture<TaggedLibraryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> WithDbAsync<T>(Func<SonNuageDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>());
    }

    [Fact]
    public async Task Scan_stores_the_musicbrainz_ids_found_in_the_tags()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);

        var track = await WithDbAsync(db => db.Tracks.Include(t => t.Album).SingleAsync(t => t.Title == "Tagged Song", Ct));
        Assert.Equal(TaggedLibraryFactory.Recording, track.MusicBrainzRecordingId);
        Assert.Equal(TaggedLibraryFactory.Release, track.MusicBrainzReleaseId);
        Assert.Equal(TaggedLibraryFactory.Release, track.Album.MusicBrainzReleaseId);

        Assert.Equal(TaggedLibraryFactory.ReleaseGroup, track.Album.MusicBrainzReleaseGroupId);

        // Les morceaux sans tags MusicBrainz restent non identifiés.
        var untagged = await WithDbAsync(db => db.Tracks.SingleAsync(t => t.Title == "Opening", Ct));
        Assert.Null(untagged.MusicBrainzRecordingId);
    }

    [Fact]
    public async Task Scan_reads_the_musicbrainz_id_of_the_artist_from_the_tags()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);

        var artist = await WithDbAsync(db => db.Artists.SingleAsync(a => a.Name == "Tagged Artist", Ct));
        Assert.Equal(TaggedLibraryFactory.ArtistMbid.ToString(), artist.MusicBrainzId);
        Assert.Equal(ArtistMbidSource.Tags, artist.MusicBrainzIdSource);
        var detail = await admin.GetFromJsonAsync<ArtistDetailDto>($"/api/artists/{artist.Id}", Ct);
        Assert.Equal(TaggedLibraryFactory.ArtistMbid.ToString(), detail!.Artist.MusicBrainzId);

        var untagged = await WithDbAsync(db => db.Artists.SingleAsync(a => a.Name == "Other Band", Ct));
        Assert.Null(untagged.MusicBrainzId);
    }

    [Fact]
    public async Task A_rescan_skips_tracks_already_identified_unless_asked()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);

        var skipping = await WithDbAsync(db => AcoustIdRescanJob.Targets(db.Tracks, artistId: null, includeIdentified: false).Select(t => t.Title).ToListAsync(Ct));
        Assert.DoesNotContain("Tagged Song", skipping);
        Assert.Contains("Opening", skipping);

        var all = await WithDbAsync(db => AcoustIdRescanJob.Targets(db.Tracks, artistId: null, includeIdentified: true).Select(t => t.Title).ToListAsync(Ct));
        Assert.Contains("Tagged Song", all);
        Assert.Equal(skipping.Count + 1, all.Count);
    }

    [Fact]
    public async Task The_admin_links_an_album_to_its_musicbrainz_release_and_can_unlink_it()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var albumId = await WithDbAsync(db => db.Albums.Where(a => a.Title == "First Album").Select(a => a.Id).SingleAsync(Ct));
        const string release = "c0ffee00-1234-4abc-8def-0123456789ab";

        var linked = await admin.PutAsJsonAsync($"/api/admin/albums/{albumId}/musicbrainz",
            new LinkAlbumReleaseRequest($"https://musicbrainz.org/release/{release}"), Ct);
        linked.EnsureSuccessStatusCode();
        Assert.Equal(Guid.Parse(release), (await linked.Content.ReadFromJsonAsync<AlbumDto>(Ct))!.MusicBrainzReleaseId);
        var detail = await admin.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{albumId}", Ct);
        Assert.Equal(Guid.Parse(release), detail!.Album.MusicBrainzReleaseId);
        Assert.True(await WithDbAsync(db => db.Tracks.Where(t => t.AlbumId == albumId).AllAsync(t => t.MusicBrainzReleaseId == Guid.Parse(release), Ct)));

        var bad = await admin.PutAsJsonAsync($"/api/admin/albums/{albumId}/musicbrainz", new LinkAlbumReleaseRequest("pas un identifiant"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var unlinked = await admin.PutAsJsonAsync($"/api/admin/albums/{albumId}/musicbrainz", new LinkAlbumReleaseRequest(null), Ct);
        Assert.Null((await unlinked.Content.ReadFromJsonAsync<AlbumDto>(Ct))!.MusicBrainzReleaseId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync($"/api/admin/albums/{Guid.NewGuid()}/musicbrainz", new LinkAlbumReleaseRequest(release), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_admin_sets_the_musicbrainz_id_of_an_artist_and_can_clear_it()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var artistId = await WithDbAsync(db => db.Artists.Where(a => a.Name == "Other Band").Select(a => a.Id).SingleAsync(Ct));
        const string mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

        var linked = await admin.PutAsJsonAsync($"/api/admin/artists/{artistId}/musicbrainz",
            new LinkArtistRequest($"https://musicbrainz.org/artist/{mbid}"), Ct);
        linked.EnsureSuccessStatusCode();
        Assert.Equal(mbid, (await linked.Content.ReadFromJsonAsync<ArtistDto>(Ct))!.MusicBrainzId);
        Assert.Equal(ArtistMbidSource.Manual, await WithDbAsync(db => db.Artists.Where(a => a.Id == artistId).Select(a => a.MusicBrainzIdSource).SingleAsync(Ct)));

        var bad = await admin.PutAsJsonAsync($"/api/admin/artists/{artistId}/musicbrainz", new LinkArtistRequest("pas un identifiant"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync($"/api/admin/artists/{Guid.NewGuid()}/musicbrainz", new LinkArtistRequest(mbid), Ct)).StatusCode);

        // Effacé : les tags ou la déduction pourront en poser un autre.
        var cleared = await admin.PutAsJsonAsync($"/api/admin/artists/{artistId}/musicbrainz", new LinkArtistRequest(""), Ct);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<ArtistDto>(Ct))!.MusicBrainzId);
        Assert.Equal(ArtistMbidSource.None, await WithDbAsync(db => db.Artists.Where(a => a.Id == artistId).Select(a => a.MusicBrainzIdSource).SingleAsync(Ct)));

        (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("mbid-user", "password-1234", Roles.User), Ct)).EnsureSuccessStatusCode();
        var user = await factory.ClientAsync("mbid-user", "password-1234");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PutAsJsonAsync($"/api/admin/artists/{artistId}/musicbrainz", new LinkArtistRequest(mbid), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("8f3471b5-7e6a-48da-86a9-c1c07a0f47ae", "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae")]
    [InlineData(" 8F3471B5-7E6A-48DA-86A9-C1C07A0F47AE ", "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae")]
    [InlineData("https://musicbrainz.org/release/1f4d3c3e-2a5b-4c6d-9e8f-0a1b2c3d4e5f/discids", "1f4d3c3e-2a5b-4c6d-9e8f-0a1b2c3d4e5f")]
    [InlineData("8f3471b5-7e6a-48da-86a9-c1c07a0f47ae/1f4d3c3e-2a5b-4c6d-9e8f-0a1b2c3d4e5f", "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae")]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("not an id", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Musicbrainz_ids_are_read_from_ids_lists_and_links(string? input, string? expected) =>
        Assert.Equal(expected is null ? null : Guid.Parse(expected), MusicBrainzId.Parse(input));

    [Fact]
    public void An_artist_id_is_replaced_only_by_a_surer_source()
    {
        var artist = new Artist { Name = "A", NormalizedName = "a" };
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();

        Assert.True(artist.OfferMusicBrainzId(first, ArtistMbidSource.Search));
        Assert.True(artist.OfferMusicBrainzId(second, ArtistMbidSource.Tags));
        // Deux fichiers tagués différemment : le premier lu reste.
        Assert.False(artist.OfferMusicBrainzId(third, ArtistMbidSource.Tags));
        Assert.False(artist.OfferMusicBrainzId(third, ArtistMbidSource.Derived));
        Assert.Equal(second.ToString(), artist.MusicBrainzId);
        Assert.True(artist.OfferMusicBrainzId(third, ArtistMbidSource.Manual));
        Assert.Equal((third.ToString(), ArtistMbidSource.Manual), (artist.MusicBrainzId, artist.MusicBrainzIdSource));
    }

    private static Track LibraryTrack(bool locked = false) => new()
    {
        FilePath = "/music/a.flac", Title = "Song", ArtistName = "Artist", AlbumArtistName = "Artist", AlbumTitle = "Album",
        Format = "flac", MetadataLocked = locked,
    };

    private static MetadataCandidateDto Candidate(string album = "Album", double score = 0.95) =>
        new("Song", "Artist", album, "Artist", null, null, null, null, score, "acoustid",
            TaggedLibraryFactory.Recording.ToString(), TaggedLibraryFactory.Release.ToString());

    [Fact]
    public void An_accepted_match_identifies_the_track_even_when_its_tags_already_agree()
    {
        var track = LibraryTrack();
        var outcome = LibraryMatchJob.Evaluate(track, Candidate(), apply: true, threshold: 0.85);

        Assert.Equal("applied", outcome.Result);
        outcome.Apply(track);
        Assert.Equal(TaggedLibraryFactory.Recording, track.MusicBrainzRecordingId);
        Assert.Equal(TaggedLibraryFactory.Release, track.MusicBrainzReleaseId);
        Assert.Equal("Song", track.Title);

        // Une fois identifié, le même résultat ne change plus rien.
        Assert.Equal("same", LibraryMatchJob.Evaluate(track, Candidate(), apply: true, threshold: 0.85).Result);
    }

    [Fact]
    public void A_match_on_another_album_keeps_the_recording_but_not_the_release()
    {
        var track = LibraryTrack();
        var outcome = LibraryMatchJob.Evaluate(track, Candidate(album: "Greatest Hits 1990-2000"), apply: true, threshold: 0.85);
        outcome.Apply(track);
        Assert.Equal(TaggedLibraryFactory.Recording, track.MusicBrainzRecordingId);
        Assert.Null(track.MusicBrainzReleaseId);
    }

    [Theory]
    [InlineData(false, false, 0.95, "proposed")]
    [InlineData(true, true, 0.95, "locked")]
    [InlineData(true, false, 0.6, "low")]
    public void Unsafe_matches_store_nothing(bool apply, bool locked, double score, string expected)
    {
        var track = LibraryTrack(locked);
        var outcome = LibraryMatchJob.Evaluate(track, Candidate(score: score), apply, threshold: 0.85);
        Assert.Equal(expected, outcome.Result);
        outcome.Apply(track);
        Assert.Null(track.MusicBrainzRecordingId);
    }

    [Fact]
    public async Task A_rescan_keeps_an_id_set_by_acoustid_when_the_tags_have_none()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var recording = Guid.NewGuid();
        var path = await WithDbAsync(async db =>
        {
            var t = await db.Tracks.SingleAsync(x => x.Title == "Opening", Ct);
            t.MusicBrainzRecordingId = recording;
            await db.SaveChangesAsync(Ct);
            return t.FilePath;
        });
        try
        {
            // Fichier modifié : le scan relit ses tags, qui n'ont pas d'identifiant MusicBrainz.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            await factory.Services.GetRequiredService<ScanCoordinator>().RunFullScanAsync(null, Ct);

            var track = await WithDbAsync(db => db.Tracks.AsNoTracking().SingleAsync(x => x.Title == "Opening", Ct));
            Assert.Equal(recording, track.MusicBrainzRecordingId);
        }
        finally
        {
            await WithDbAsync(db => db.Tracks.Where(x => x.Title == "Opening")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.MusicBrainzRecordingId, (Guid?)null), Ct));
        }
    }
}
