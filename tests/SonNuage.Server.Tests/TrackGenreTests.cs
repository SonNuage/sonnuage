using SonNuage.Server.Radio;

namespace SonNuage.Server.Tests;

/// <summary>Genre d'un morceau pour les radios : styles Discogs de l'analyse d'abord, sinon genre du fichier et tags.</summary>
public sealed class TrackGenreTests
{
    [Fact]
    public void The_analysis_styles_give_the_genre_keeping_the_likely_ones()
    {
        var g = TrackGenre.From("Rock---Rock & Roll:0.6|Rock---Rockabilly:0.35|Pop---Vocal:0.1", "Pop", null);

        Assert.Equal(["rock"], g.Genres.Order());
        Assert.Equal(["rock roll", "rockabilly"], g.Styles.Order());
    }

    [Fact]
    public void The_analysis_wins_over_a_file_genre()
    {
        Assert.Equal(["rock"], TrackGenre.From("Rock---Hard Rock:0.5", "Classic Rock;Electronic;Pop", ["dance-pop"]).Genres);
    }

    [Theory]
    [InlineData("Blues Rock", "rock", "blues rock")]
    [InlineData("Pop Rock;Rock;Rock And Roll", "rock", "pop rock")]
    [InlineData("Heavy Metal", "rock", "heavy metal")]
    [InlineData("Metal", "rock", "heavy metal")]
    [InlineData("Hip Hop", "hip hop", null)]
    [InlineData("Soundtrack", "stage screen", "soundtrack")]
    [InlineData("Synth-Pop", "electronic", "synth pop")]
    [InlineData("R&B", "funk soul", "contemporary r b")]
    [InlineData("Folk, World, & Country", "folk world country", null)]
    public void Without_analysis_the_file_genre_is_read_in_the_same_vocabulary(string file, string genre, string? style)
    {
        var g = TrackGenre.From(null, file, null);

        Assert.Contains(genre, g.Genres);
        if (style is not null) Assert.Contains(style, g.Styles);
    }

    [Fact]
    public void Musicbrainz_tags_help_and_noise_is_ignored()
    {
        Assert.Equal(["electronic", "pop"], TrackGenre.From(null, null, ["pop", "electropop", "english", "offizielle charts"]).Genres.Order());
        Assert.False(TrackGenre.From(null, "Other", ["5+ wochen"]).Known);
        Assert.False(TrackGenre.From(null, null, null).Known);
    }
}
