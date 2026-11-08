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

        // Les morceaux sans tags MusicBrainz restent non identifiés.
        var untagged = await WithDbAsync(db => db.Tracks.SingleAsync(t => t.Title == "Opening", Ct));
        Assert.Null(untagged.MusicBrainzRecordingId);
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
}
