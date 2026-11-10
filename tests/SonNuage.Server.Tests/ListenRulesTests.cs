using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>Quand une lecture compte : dans l'historique (30 s ou la moitié) et pour ListenBrainz (4 min ou la moitié).</summary>
public class ListenRulesTests
{
    [Theory]
    [InlineData(29, 300, false)]   // passée trop tôt : pas une écoute
    [InlineData(30, 300, true)]    // 30 s suffisent pour un long morceau
    [InlineData(10, 20, true)]     // la moitié d'un morceau court
    [InlineData(9, 20, false)]
    [InlineData(29, 0, false)]     // durée inconnue : 30 s
    [InlineData(30, 0, true)]
    public void A_play_enters_the_history_after_30_seconds_or_half_the_track(double seconds, double duration, bool expected) =>
        Assert.Equal(expected, ListenRules.CountsAsPlay(seconds, duration));

    [Theory]
    [InlineData(239, 600, false)]
    [InlineData(240, 600, true)]   // 4 minutes d'un morceau de 10 minutes
    [InlineData(90, 180, true)]    // la moitié d'un morceau de 3 minutes
    [InlineData(89, 180, false)]
    [InlineData(239, 0, false)]    // durée inconnue : 4 minutes
    [InlineData(240, 0, true)]
    public void A_listen_counts_for_ListenBrainz_after_half_the_track_or_4_minutes(double seconds, double duration, bool expected) =>
        Assert.Equal(expected, ListenRules.CountsForListenBrainz(seconds, duration));

    [Fact]
    public void Nonsense_durations_never_count() =>
        Assert.False(ListenRules.CountsForListenBrainz(double.NaN, 180) || ListenRules.CountsAsPlay(-5, 180));
}
