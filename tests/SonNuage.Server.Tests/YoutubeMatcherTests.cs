using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Note d'un résultat YouTube pour « artiste + titre » : titre, artiste, chaîne, durée, versions écartées.</summary>
public sealed class YoutubeMatcherTests
{
    private static YtEntry Video(string title, string? channel, double? duration, string id = "aaaaaaaaaa1") =>
        new(id, title, channel, duration, null);

    private static readonly MatchQuery Query = new("Daft Punk", "Get Lucky", 248);

    [Fact]
    public void The_official_audio_with_the_right_duration_is_a_sure_match()
    {
        var topic = YoutubeMatcher.Score(Query, Video("Get Lucky", "Daft Punk - Topic", 249));
        var official = YoutubeMatcher.Score(Query, Video("Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams, Nile Rodgers", "Daft Punk", 248));

        Assert.True(topic >= YoutubeMatcher.SureScore, $"topic {topic}");
        Assert.True(official >= YoutubeMatcher.SureScore, $"official {official}");
    }

    [Fact]
    public void Live_remix_karaoke_and_covers_are_pushed_down_unless_asked()
    {
        var studio = YoutubeMatcher.Score(Query, Video("Daft Punk - Get Lucky", "Daft Punk", 248));
        foreach (var version in new[] { "Daft Punk - Get Lucky (Live at the Grammys)", "Get Lucky (Remix)", "Get Lucky - Karaoke Version",
                     "Get Lucky (Daft Punk cover)", "Get Lucky sped up", "Get Lucky (Nightcore)" })
            Assert.True(YoutubeMatcher.Score(Query, Video(version, "Daft Punk", 248)) < studio - 30, version);

        var live = new MatchQuery("Daft Punk", "Get Lucky (Live)", null);
        Assert.True(YoutubeMatcher.Score(live, Video("Daft Punk - Get Lucky (Live at the Grammys)", "Daft Punk", 600)) >= 60);
    }

    [Fact]
    public void Karaoke_channels_and_tributes_never_pass_for_the_song()
    {
        // Relevés en production : des reprises qui mettaient le nom de l'artiste dans leur titre.
        var cases = new (MatchQuery Query, string Title)[]
        {
            (new("Ariana Grande", "7 rings", 178), "7 rings (made popular by Ariana Grande) [vocal version]"),
            (new("Ariana Grande", "positions", 172), "positions (Originally Performed by Ariana Grande)"),
            (new("Halsey", "Without Me", 201), "Without Me (Lower Key) [Originally Performed by Halsey]"),
            (new("Lady Gaga", "Shallow", 216), "Shallow (As Originally Performed By Lady Gaga & Bradley Cooper)"),
            (new("twenty one pilots", "Heathens", 195), "Heathens - Tribute to Twenty One Pilots"),
            (new("Justin Bieber", "Beauty And A Beat", 228), "Beauty And A Beat (In the Style of Justin Bieber)"),
            (new("Madonna", "La Isla Bonita", 242), "La Isla Bonita (Made Famous By Madonna) [Backing Track]"),
        };
        foreach (var (query, title) in cases)
            Assert.True(YoutubeMatcher.Score(query, Video(title, null, query.DurationSeconds)) < 60, title);
    }

    [Fact]
    public void A_far_duration_or_another_artist_lowers_the_score()
    {
        var right = YoutubeMatcher.Score(Query, Video("Daft Punk - Get Lucky", "Daft Punk", 248));
        var extended = YoutubeMatcher.Score(Query, Video("Daft Punk - Get Lucky", "Daft Punk", 369));
        var other = YoutubeMatcher.Score(Query, Video("Get Lucky", "Someone Else", 248));
        var unrelated = YoutubeMatcher.Score(Query, Video("Harder Better Faster Stronger", "Daft Punk", 224));

        Assert.True(extended < right - 15, $"{extended} vs {right}");
        Assert.True(other < YoutubeMatcher.SureScore, $"other {other}");
        Assert.True(unrelated < 50, $"unrelated {unrelated}");
    }

    [Fact]
    public void Accents_case_and_punctuation_do_not_matter()
    {
        var query = new MatchQuery("Stromae", "Alors on danse", null);
        Assert.True(YoutubeMatcher.Score(query, Video("STROMAE – ALORS ON DANSE (Clip Officiel)", "Stromae", 207)) >= YoutubeMatcher.SureScore);
        var accented = new MatchQuery("Zaz", "Je veux", null);
        Assert.True(YoutubeMatcher.Score(accented, Video("ZAZ - Je Veux (Clip officiel)", "ZAZ", 220)) >= YoutubeMatcher.SureScore);
    }

    [Fact]
    public void Candidates_are_ranked_best_first_and_a_high_score_is_sure()
    {
        var ranked = YoutubeMatcher.Rank(Query, [
            Video("Get Lucky (Live)", "Daft Punk", 600, "aaaaaaaaaa1"),
            Video("Get Lucky", "Daft Punk - Topic", 248, "aaaaaaaaaa2"),
            Video("Get Lucky", "Someone Else", 190, "aaaaaaaaaa3"),
        ]);

        Assert.Equal(["aaaaaaaaaa2", "aaaaaaaaaa3", "aaaaaaaaaa1"], ranked.Select(r => r.Entry.Id));
        Assert.True(YoutubeMatcher.IsSure(ranked));
        Assert.False(YoutubeMatcher.IsSure([]));
    }

    [Theory]
    [InlineData("Madonna", "Madonna", false, true)]
    [InlineData("Eros Ramazzotti", "ErosRamazzotti", false, true)]
    [InlineData("Lady Gaga", "LadyGagaVEVO", false, true)]
    [InlineData("Queen", "Queen Official", false, true)]
    [InlineData("Queen", "This Is Queen", false, true)]
    [InlineData("Arctic Monkeys", "Official Arctic Monkeys", false, true)]
    [InlineData("Daft Punk", "Daft Punk - Topic", false, true)]
    [InlineData("Red Hot Chili Peppers", "Red Hot Chili Peppers Music", true, true)]
    [InlineData("Lady Gaga", "Pizza Music", false, false)]
    [InlineData("Lady Gaga", "GagaTube GagaTube", false, false)]
    [InlineData("Halsey", "ChainsmokersVEVO", false, false)]
    [InlineData("Queen", "Queen Latifah", false, false)]
    [InlineData("Lorde", "Lorde Lyrics Fan Channel", false, false)]
    [InlineData("Madonna", null, false, false)]
    public void The_official_channel_of_the_artist_is_recognised(string artist, string? channel, bool verified, bool official) =>
        Assert.Equal(official, YoutubeMatcher.IsOfficial(artist, new YtEntry("aaaaaaaaaa1", "x", channel, null, null) { ChannelVerified = verified }));

    [Fact]
    public void The_official_channel_comes_first_when_its_video_is_good_enough()
    {
        var query = new MatchQuery("Lady Gaga", "Poker Face", 237);
        var lyrics = Video("Lady Gaga - Poker Face (Lyrics)", "Pizza Music", 237, "aaaaaaaaaa1");
        var clip = Video("Lady Gaga - Poker Face (Official Music Video)", "LadyGagaVEVO", 215 + 60, "aaaaaaaaaa2");
        var offTopic = Video("Lady Gaga - Bad Romance (Official Music Video)", "LadyGagaVEVO", 308, "aaaaaaaaaa3");
        Assert.True(YoutubeMatcher.Score(query, lyrics) > YoutubeMatcher.Score(query, clip));

        var ranked = YoutubeMatcher.Rank(query, [lyrics, offTopic, clip], officialMinScore: 60);
        Assert.Equal(["aaaaaaaaaa2", "aaaaaaaaaa1", "aaaaaaaaaa3"], ranked.Select(r => r.Entry.Id));

        // Seuil plus haut que le clip officiel : l'ordre des notes reprend le dessus.
        var strict = YoutubeMatcher.Rank(query, [lyrics, offTopic, clip], officialMinScore: 95);
        Assert.Equal("aaaaaaaaaa1", strict[0].Entry.Id);
    }

    [Fact]
    public void The_expected_duration_is_the_typical_length_not_the_first_one()
    {
        // MusicBrainz : la version longue arrivait en premier (4:45 pour La Isla Bonita, 4:03 partout ailleurs).
        Assert.Equal(243, YoutubeMatcher.TypicalDuration([285, 243, 242, 244, 360]));
        Assert.Equal(200, YoutubeMatcher.TypicalDuration([200]));
        Assert.Null(YoutubeMatcher.TypicalDuration([]));
    }
}
