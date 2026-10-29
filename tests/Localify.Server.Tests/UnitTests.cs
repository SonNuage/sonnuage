using Localify.Contracts;
using Localify.Server.Data;
using Localify.Server.Library;
using Localify.Server.Streaming;
using Localify.UI.Services;

namespace Localify.Server.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData("Au‐delà du délire", "Au-Delà du Délire")]
    [InlineData("Get a Grip", "Get a Grip [Bonus Track]")]
    [InlineData("AC/DC", "ac dc")]
    public void Key_groups_equivalent_names(string a, string b) => Assert.Equal(NameNormalizer.Key(a), NameNormalizer.Key(b));

    [Theory]
    [InlineData("1._It's_a_Long_Way_to_the_Top.mp3", "It's a Long Way to the Top")]
    [InlineData("12 Crazy.wav", "Crazy")]
    [InlineData("David Gilmour - About Face - Blue Light.mp3", "Blue Light", "David Gilmour - About Face - ")]
    [InlineData("Vol. 2", "Vol. 2")]
    public void CleanTitle_strips_numbers_prefixes_and_underscores(string input, string expected, string? prefix = null) =>
        Assert.Equal(expected, NameNormalizer.CleanTitle(input, prefix is null ? [] : [prefix]));

    [Theory]
    [InlineData("01 - Intro.mp3", 1)]
    [InlineData("5._Livin'_on_the_Edge.wav", 5)]
    [InlineData("Intro.mp3", null)]
    public void TrackNumberFromFileName(string name, int? expected) => Assert.Equal(expected, NameNormalizer.TrackNumberFromFileName(name));

    [Fact]
    public void Cover_rank_prefers_front_over_back()
    {
        Assert.True(LibraryFiles.CoverRank("Folder.jpg") > LibraryFiles.CoverRank("AlbumArtSmall.jpg"));
        Assert.True(LibraryFiles.CoverRank("00_the_beatles_-_love-cd-2006-front.jpg") > LibraryFiles.CoverRank("00_the_beatles_-_love-cd-2006-back.jpg"));
        Assert.True(LibraryFiles.CoverRank("AlbumArt_{X}_Large.jpg") > LibraryFiles.CoverRank("AlbumArt_{X}_Small.jpg"));
    }
}

public class TranscodeProfileTests
{
    private static Track Make(string format, bool lossless, int? bitrate) => new()
    {
        FilePath = $"/x.{format}", Title = "t", ArtistName = "a", AlbumArtistName = "a", AlbumTitle = "b",
        Format = format, IsLossless = lossless, BitrateKbps = bitrate,
    };

    [Fact]
    public void Original_quality_serves_playable_files_as_is() =>
        Assert.Null(TranscodeProfile.Resolve(Make("flac", true, 900), StreamQuality.Original));

    [Fact]
    public void Unplayable_formats_are_always_transcoded()
    {
        Assert.Equal(StreamQuality.Lossless, TranscodeProfile.Resolve(Make("ape", true, 900), StreamQuality.Original)!.Quality);
        Assert.Equal(StreamQuality.High, TranscodeProfile.Resolve(Make("wma", false, 128), StreamQuality.Original)!.Quality);
    }

    [Fact]
    public void Lossy_sources_are_never_upscaled()
    {
        Assert.Null(TranscodeProfile.Resolve(Make("mp3", false, 128), StreamQuality.High));
        Assert.Null(TranscodeProfile.Resolve(Make("mp3", false, 192), StreamQuality.Lossless));
        Assert.Equal(StreamQuality.Low, TranscodeProfile.Resolve(Make("mp3", false, 320), StreamQuality.Low)!.Quality);
    }

    [Fact]
    public void Wav_to_lossless_becomes_flac_but_flac_stays_original()
    {
        Assert.Equal("flac", TranscodeProfile.Resolve(Make("wav", true, 1411), StreamQuality.Lossless)!.Extension);
        Assert.Null(TranscodeProfile.Resolve(Make("flac", true, 900), StreamQuality.Lossless));
    }

    [Fact]
    public void Mp3_profile_resamples_high_resolution_sources()
    {
        var args = TranscodeProfile.Resolve(Make("flac", true, 4000), StreamQuality.Normal)!.FfmpegArguments("in", "out", 96_000).ToList();
        Assert.Contains("44100", args);
        Assert.Contains("192k", args);
    }
}

public class PlayQueueTests
{
    private static List<TrackDto> Tracks(int n) => Enumerable.Range(1, n).Select(i => new TrackDto(
        Guid.NewGuid(), $"T{i}", Guid.Empty, "A", Guid.Empty, "B", i, 1, 60, "mp3", false, 192, false, false)).ToList();

    [Fact]
    public void Next_stops_at_end_without_repeat_and_wraps_with_repeat_all()
    {
        var q = new PlayQueue();
        q.Load(Tracks(3), 2, false);
        Assert.Equal(-1, q.PeekNext(auto: true));
        q.Repeat = RepeatMode.All;
        Assert.Equal(0, q.PeekNext(auto: true));
    }

    [Fact]
    public void Repeat_one_only_applies_to_natural_end()
    {
        var q = new PlayQueue { Repeat = RepeatMode.One };
        q.Load(Tracks(3), 1, false);
        Assert.Equal(1, q.PeekNext(auto: true));
        Assert.Equal(2, q.PeekNext(auto: false));
    }

    [Fact]
    public void Shuffle_keeps_current_track_first_and_unshuffle_restores_order()
    {
        var tracks = Tracks(50);
        var q = new PlayQueue();
        q.Load(tracks, 10, false);
        q.SetShuffle(true);
        Assert.Equal(tracks[10].Id, q.Items[0].Id);
        Assert.Equal(0, q.Index);
        Assert.Equal(tracks.Select(t => t.Id).Order(), q.Items.Select(t => t.Id).Order());

        q.MoveTo(5);
        var current = q.Current!.Id;
        q.SetShuffle(false);
        Assert.Equal(tracks.Select(t => t.Id), q.Items.Select(t => t.Id));
        Assert.Equal(current, q.Current!.Id);
    }

    [Fact]
    public void Upcoming_follows_play_order_for_prefetch()
    {
        var tracks = Tracks(5);
        var q = new PlayQueue();
        q.Load(tracks, 3, false);
        Assert.Equal([tracks[4].Id], q.Upcoming(3).Select(t => t.Id));
        q.Repeat = RepeatMode.All;
        Assert.Equal([tracks[4].Id, tracks[0].Id, tracks[1].Id], q.Upcoming(3).Select(t => t.Id));
    }

    [Fact]
    public void Play_next_inserts_after_current()
    {
        var tracks = Tracks(3);
        var extra = Tracks(1)[0];
        var q = new PlayQueue();
        q.Load(tracks, 0, false);
        q.Enqueue([extra], playNext: true);
        Assert.Equal(extra.Id, q.Items[1].Id);
        q.RemoveAt(0);
        Assert.Equal(extra.Id, q.Items[0].Id);
    }
}
