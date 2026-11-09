using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>« Il y a 5 min », « il y a 3 h », « il y a 2 jours » ; au-delà d'un mois, la date.</summary>
public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(20, "format.ago.now", 0)]
    [InlineData(5 * 60, "format.ago.minutes", 5)]
    [InlineData(59 * 60 + 59, "format.ago.minutes", 59)]
    [InlineData(3 * 3600 + 1800, "format.ago.hours", 3)]
    [InlineData(2 * 86400 + 3600, "format.ago.days", 2)]
    [InlineData(29 * 86400, "format.ago.days", 29)]
    public void Recent_times_are_said_relative_to_now(int secondsAgo, string key, int count)
    {
        Assert.Equal((key, count), RelativeTime.Of(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Older_than_a_month_falls_back_to_the_date()
    {
        Assert.Null(RelativeTime.Of(Now.AddDays(-31), Now));
    }

    [Fact]
    public void A_clock_slightly_ahead_on_the_server_reads_as_just_now()
    {
        Assert.Equal(("format.ago.now", 0), RelativeTime.Of(Now.AddSeconds(30), Now));
    }
}
