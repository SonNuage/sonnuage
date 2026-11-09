using SonNuage.Contracts;
using SonNuage.Server.Lyrics;

namespace SonNuage.Server.Tests;

/// <summary>Fichiers LRC : balises de temps, métadonnées, décalage.</summary>
public sealed class LrcParserTests
{
    [Fact]
    public void Timed_lines_are_read_in_seconds()
    {
        var lyrics = LrcParser.Parse("[00:12.34]Bonjour\n[01:02.5]Au revoir\n[02:03]Fin", "local");

        Assert.True(lyrics.Synced);
        Assert.Equal("local", lyrics.Source);
        Assert.Equal([new LyricLineDto(12.34, "Bonjour"), new LyricLineDto(62.5, "Au revoir"), new LyricLineDto(123, "Fin")], lyrics.Lines);
    }

    [Fact]
    public void A_line_with_several_timestamps_repeats_in_time_order()
    {
        var lyrics = LrcParser.Parse("[00:30.00][00:10.00]Refrain\n[00:20.00]Couplet", null);

        Assert.Equal([new LyricLineDto(10, "Refrain"), new LyricLineDto(20, "Couplet"), new LyricLineDto(30, "Refrain")], lyrics.Lines);
    }

    [Fact]
    public void Metadata_tags_and_untimed_lines_are_ignored_in_a_synced_file()
    {
        var lyrics = LrcParser.Parse("[ar:Artiste]\r\n[ti:Titre]\r\n[length:03:20]\r\nnote\r\n[00:01.000]  Un  \r\n[00:02.00]", null);

        Assert.Equal([new LyricLineDto(1, "Un"), new LyricLineDto(2, "")], lyrics.Lines);
    }

    [Theory]
    [InlineData("[offset:+500]", 9.5)]
    [InlineData("[offset:-250]", 10.25)]
    [InlineData("[offset:abc]", 10)]
    public void Offset_moves_every_line_and_a_positive_one_shows_lyrics_sooner(string offset, double expected)
    {
        var lyrics = LrcParser.Parse($"{offset}\n[00:10.00]Ligne", null);

        Assert.Equal(expected, Assert.Single(lyrics.Lines).StartSeconds);
    }

    [Fact]
    public void Offset_never_goes_below_zero()
    {
        var lyrics = LrcParser.Parse("[offset:+5000]\n[00:01.00]Ligne", null);

        Assert.Equal(0, Assert.Single(lyrics.Lines).StartSeconds);
    }

    [Fact]
    public void Word_timestamps_of_enhanced_lrc_are_removed()
    {
        var lyrics = LrcParser.Parse("[00:01.00]<00:01.00>Un <00:01.50>deux", null);

        Assert.Equal("Un deux", Assert.Single(lyrics.Lines).Text);
    }

    [Fact]
    public void Text_without_timestamps_is_plain_lyrics()
    {
        var lyrics = LrcParser.Parse("[ar:Artiste]\n\nPremière\n\nSeconde\n\n", "lrclib");

        Assert.False(lyrics.Synced);
        Assert.Equal([new LyricLineDto(null, "Première"), new LyricLineDto(null, ""), new LyricLineDto(null, "Seconde")], lyrics.Lines);
    }

    [Fact]
    public void Empty_text_has_no_lines()
    {
        Assert.Empty(LrcParser.Parse("[ar:Artiste]\n\n", null).Lines);
    }
}
