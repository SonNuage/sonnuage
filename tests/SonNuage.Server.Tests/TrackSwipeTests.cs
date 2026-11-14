using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Balayer le bandeau de lecture : vers la gauche, titre suivant ; vers la droite, titre précédent.</summary>
public sealed class TrackSwipeTests
{
    [Theory]
    [InlineData(-120, 10, SwipeAction.Next)]
    [InlineData(120, -10, SwipeAction.Previous)]
    [InlineData(-30, 0, SwipeAction.None)]      // simple toucher qui a glissé
    [InlineData(-120, 90, SwipeAction.None)]    // geste surtout vertical : on fait défiler la page
    [InlineData(0, 0, SwipeAction.None)]
    public void A_mostly_horizontal_swipe_long_enough_changes_track(double dx, double dy, SwipeAction expected) =>
        Assert.Equal(expected, TrackSwipe.Classify(dx, dy));

    [Theory]
    [InlineData(-40, 5, -40)]
    [InlineData(40, 60, 0)]
    public void The_bar_follows_the_finger_only_sideways(double dx, double dy, double expected) =>
        Assert.Equal(expected, TrackSwipe.Offset(dx, dy));
}
