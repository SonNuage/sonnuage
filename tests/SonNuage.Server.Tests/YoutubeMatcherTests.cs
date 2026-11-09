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
}
