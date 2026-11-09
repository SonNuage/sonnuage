using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Ligne de paroles en cours, entre deux positions rapportées par le lecteur (une par seconde).</summary>
public sealed class LyricsTimingTests
{
    private static readonly IReadOnlyList<LyricLineDto> Lines =
        [new(1, "Un"), new(3.5, "Deux"), new(3.5, "Deux bis"), new(8, "Trois")];

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(3.4, 0)]
    [InlineData(3.5, 2)]
    [InlineData(7.99, 2)]
    [InlineData(100, 3)]
    public void The_current_line_is_the_last_one_started(double position, int expected) =>
        Assert.Equal(expected, LyricsTiming.CurrentIndex(Lines, position));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 0.5)]
    [InlineData(3.5, 4.5)]
    public void The_next_change_comes_when_the_next_line_starts(double position, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), LyricsTiming.UntilNext(Lines, position));

    [Fact]
    public void After_the_last_line_nothing_changes() => Assert.Null(LyricsTiming.UntilNext(Lines, 8));

    [Fact]
    public void Position_moves_on_between_two_reports_while_playing()
    {
        var time = new Clock();
        var clock = new LyricsClock(time);
        clock.Observe(State(10, playing: true));

        time.Now += TimeSpan.FromMilliseconds(600);
        Assert.Equal(10.6, clock.Position, 3);

        clock.Observe(State(11, playing: true));
        Assert.Equal(11, clock.Position, 3);
    }

    [Fact]
    public void Position_stands_still_when_paused_or_buffering()
    {
        var time = new Clock();
        var clock = new LyricsClock(time);
        clock.Observe(State(10, playing: false));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(10, clock.Position);

        clock.Observe(State(10, playing: true, buffering: true));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(10, clock.Position);
    }

    [Fact]
    public void Without_news_from_the_player_the_estimate_stops_after_two_seconds()
    {
        var time = new Clock();
        var clock = new LyricsClock(time);
        clock.Observe(State(10, playing: true));

        time.Now += TimeSpan.FromSeconds(30);

        Assert.Equal(12, clock.Position);
    }

    [Fact]
    public void Resuming_starts_from_where_the_pause_left()
    {
        var time = new Clock();
        var clock = new LyricsClock(time);
        clock.Observe(State(10, playing: false));
        time.Now += TimeSpan.FromSeconds(30);

        clock.Observe(State(10, playing: true));
        time.Now += TimeSpan.FromMilliseconds(500);

        Assert.Equal(10.5, clock.Position, 3);
    }

    private static PlayerState State(double position, bool playing, bool buffering = false) =>
        PlayerState.Empty with { PositionSeconds = position, IsPlaying = playing, IsBuffering = buffering };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
