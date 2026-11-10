using SonNuage.Server.Radio;
using SonNuage.Server.Search;

namespace SonNuage.Server.Tests;

/// <summary>Recherche par ambiance : lecture d'une demande en langage courant (« musique calme pour travailler »).</summary>
public sealed class AmbianceParserTests
{
    private static readonly string[] Genres = ["Rock", "Pop", "Hard Rock", "Hip-Hop", "Jazz; Blues", "Musique classique", "Electronic/Dance"];

    private static AmbianceQuery Parsed(string query) =>
        AmbianceParser.Parse(query, Genres) ?? throw new Xunit.Sdk.XunitException($"« {query} » non reconnue comme ambiance");

    private static string[] Moods(string query) => Parsed(query).Moods.Select(m => m.Id).Order().ToArray();

    [Theory]
    [InlineData("musique calme pour travailler", "calm", "focus")]
    [InlineData("énergique pour courir", "energetic", "workout")]
    [InlineData("triste", "sad")]
    [InlineData("chill du dimanche", "calm")]
    [InlineData("Mélancolique", "sad")]
    [InlineData("MUSIQUE CALME", "calm")]
    [InlineData("quelque chose de joyeux pour la fête", "happy", "party")]
    [InlineData("musique douce pour dormir", "calm", "sleep")]
    [InlineData("chansons d'amour", "romantic")]
    [InlineData("morceaux dansants et rapides", "dance", "fast")]
    [InlineData("musique pour se concentrer", "focus")]
    [InlineData("musique instrumentale pour étudier", "focus", "instrumental")]
    public void French_requests_are_understood(string query, params string[] moods) =>
        Assert.Equal(moods.Order(), Moods(query));

    [Theory]
    [InlineData("calm music for working", "calm", "focus")]
    [InlineData("upbeat songs for running", "energetic", "workout")]
    [InlineData("sad", "sad")]
    [InlineData("chill sunday vibes", "calm")]
    [InlineData("happy party music", "happy", "party")]
    [InlineData("something relaxing to sleep", "calm", "sleep")]
    [InlineData("angry workout songs", "aggressive", "workout")]
    [InlineData("slow acoustic love songs", "acoustic", "romantic", "slow")]
    public void English_requests_are_understood(string query, params string[] moods) =>
        Assert.Equal(moods.Order(), Moods(query));

    [Fact]
    public void Recognized_words_are_kept_as_typed()
    {
        Assert.Equal(["calme", "travailler"], Parsed("musique calme pour travailler").Terms);
        Assert.Equal(["rock", "énergique", "années 90"], Parsed("rock énergique années 90").Terms);
    }

    [Fact]
    public void Genre_mood_and_decade_combine()
    {
        var q = Parsed("rock énergique années 90");
        Assert.Equal(["energetic"], q.Moods.Select(m => m.Id));
        Assert.Equal("rock", Assert.Single(q.Genres).Name);
        Assert.Equal(new YearRange(1990, 1999), q.Years);
    }

    [Theory]
    [InlineData("années 80", 1980)]
    [InlineData("les années 1970", 1970)]
    [InlineData("années 2000", 2000)]
    [InlineData("annees 60", 1960)]
    [InlineData("80s", 1980)]
    [InlineData("90's", 1990)]
    [InlineData("2010s", 2010)]
    [InlineData("eighties", 1980)]
    [InlineData("des années 10", 2010)]
    public void Decades_are_read_in_both_languages(string query, int from) =>
        Assert.Equal(new YearRange(from, from + 9), Parsed(query).Years);

    [Fact]
    public void Library_genres_of_several_words_are_recognized()
    {
        Assert.Equal("hip hop", Assert.Single(Parsed("hip hop festif").Genres).Name);
        Assert.Equal("musique classique", Assert.Single(Parsed("musique classique calme").Genres).Name);
        // « Jazz; Blues » : deux genres.
        Assert.Equal("blues", Assert.Single(Parsed("blues triste").Genres).Name);
    }

    [Fact]
    public void Common_genre_names_are_translated()
    {
        var rap = Assert.Single(Parsed("rap énergique").Genres);
        Assert.Contains("hip hop", rap.Fragments);
        Assert.Contains("classical", Assert.Single(Parsed("classique calme").Genres).Fragments);
        Assert.Contains("electronic", Assert.Single(Parsed("électro festive").Genres).Fragments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Metallica")]
    [InlineData("Second Song")]
    [InlineData("calme metallica")]
    [InlineData("pas triste")]
    [InlineData("rock")]
    [InlineData("hard rock")]
    [InlineData("musique")]
    [InlineData("pour la")]
    [InlineData("1999")]
    [InlineData("années 95")]
    [InlineData("highway to hell")]
    public void Plain_names_are_not_ambiances(string query) =>
        Assert.Null(AmbianceParser.Parse(query, Genres));

    [Fact]
    public void No_vocabulary_word_is_also_ignored()
    {
        var stop = AmbianceVocabulary.Stopwords.Select(TrackProfile.Fold).ToHashSet();
        var both = AmbianceVocabulary.Words.SelectMany(w => w.Forms).Select(TrackProfile.Fold).Where(stop.Contains).ToList();
        Assert.Empty(both);
    }

    [Fact]
    public void The_key_is_the_folded_request() =>
        Assert.Equal("rock energique annees 90", Parsed("  Rock  ÉNERGIQUE, années 90 ").Key);
}

/// <summary>Quels morceaux répondent à une ambiance.</summary>
public sealed class AmbianceScoreTests
{
    private static readonly string[] Genres = ["Rock", "Hard Rock", "Pop"];

    private static TrackProfile Track(string? genre = null, AudioFeel? feel = null, string[]? tags = null, int? year = null) =>
        TrackProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), true, 200, "Song", "Album", genre, tags, feel, year);

    private static bool Matches(string query, TrackProfile p)
    {
        var q = AmbianceParser.Parse(query, Genres)!;
        return q.Score(p) >= AmbianceQuery.Threshold;
    }

    private static readonly AudioFeel Calm = new(Bpm: 80, Relaxed: 0.9, Energy: 0.15, Acoustic: 0.7, Aggressive: 0.05, Instrumental: 0.8,
        Sad: 0.3, Happy: 0.4, Valence: 0.5, Danceability: 0.2, Party: 0.1);

    private static readonly AudioFeel Loud = new(Bpm: 150, Relaxed: 0.1, Energy: 0.95, Acoustic: 0.05, Aggressive: 0.8, Instrumental: 0.1,
        Sad: 0.1, Happy: 0.5, Valence: 0.6, Danceability: 0.7, Party: 0.6);

    [Fact]
    public void Audio_analysis_decides_the_mood()
    {
        Assert.True(Matches("musique calme pour travailler", Track(feel: Calm)));
        Assert.False(Matches("musique calme pour travailler", Track(feel: Loud)));
        Assert.True(Matches("énergique pour courir", Track(feel: Loud)));
        Assert.False(Matches("énergique pour courir", Track(feel: Calm)));
    }

    [Fact]
    public void Tracks_without_analysis_match_through_their_tags_or_genre()
    {
        Assert.True(Matches("chill", Track(tags: ["chillout"])));
        Assert.True(Matches("calme", Track(genre: "Ambient")));
        Assert.False(Matches("calme", Track(genre: "Rock")));
        Assert.False(Matches("calme", Track()));
    }

    [Fact]
    public void Genre_and_decade_are_requirements()
    {
        var nineties = Track(genre: "Hard Rock", feel: Loud, year: 1994);
        Assert.True(Matches("rock énergique années 90", nineties));
        Assert.False(Matches("rock énergique années 90", Track(genre: "Hard Rock", feel: Loud, year: 1985)));
        Assert.False(Matches("rock énergique années 90", Track(genre: "Hard Rock", feel: Loud)));
        Assert.False(Matches("rock énergique années 90", Track(genre: "Pop", feel: Loud, year: 1994)));
        // Genre trouvé dans les tags MusicBrainz quand le fichier n'en a pas.
        Assert.True(Matches("rock énergique années 90", Track(tags: ["rock"], feel: Loud, year: 1994)));
    }

    [Fact]
    public void A_decade_alone_takes_every_track_of_the_decade()
    {
        Assert.True(Matches("années 80", Track(year: 1983)));
        Assert.False(Matches("années 80", Track(year: 1993)));
    }
}
