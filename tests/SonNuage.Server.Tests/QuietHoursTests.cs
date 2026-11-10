using SonNuage.Contracts;
using SonNuage.Server.Jobs;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class QuietHoursTests
{
    private static QuietHoursDto Window(int from, int to, string zone = "UTC", bool enabled = true) =>
        new(enabled, new TimeOnly(from, 0), new TimeOnly(to, 0), [JobLanes.Analysis], zone);

    private static DateTimeOffset Utc(int hour, int minute = 0) => new(2026, 11, 10, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_daytime_window_holds_its_lanes_until_it_ends()
    {
        var q = Window(8, 23);
        Assert.Equal(Utc(23), QuietHours.Until(q, JobLanes.Analysis, Utc(8)));
        Assert.Equal(Utc(23), QuietHours.Until(q, JobLanes.Analysis, Utc(14, 30)));
        Assert.Null(QuietHours.Until(q, JobLanes.Analysis, Utc(23)));
        Assert.Null(QuietHours.Until(q, JobLanes.Analysis, Utc(7, 59)));
    }

    [Fact]
    public void A_window_can_cross_midnight()
    {
        var q = Window(22, 7);
        Assert.Equal(Utc(7).AddDays(1), QuietHours.Until(q, JobLanes.Analysis, Utc(23)));
        Assert.Equal(Utc(7), QuietHours.Until(q, JobLanes.Analysis, Utc(3)));
        Assert.Null(QuietHours.Until(q, JobLanes.Analysis, Utc(12)));
    }

    [Fact]
    public void Only_the_chosen_lanes_of_an_enabled_window_are_held()
    {
        Assert.Null(QuietHours.Until(Window(8, 23), JobLanes.Download, Utc(12)));
        Assert.Null(QuietHours.Until(Window(8, 23, enabled: false), JobLanes.Analysis, Utc(12)));
        // Début = fin : aucune plage.
        Assert.Null(QuietHours.Until(Window(8, 8), JobLanes.Analysis, Utc(8)));
    }

    [Fact]
    public void The_window_follows_its_time_zone()
    {
        // 8 h – 23 h à Paris (UTC+1 en novembre) : de 7 h à 22 h UTC.
        var q = Window(8, 23, "Europe/Paris");
        Assert.Null(QuietHours.Until(q, JobLanes.Analysis, Utc(6, 30)));
        Assert.Equal(Utc(22), QuietHours.Until(q, JobLanes.Analysis, Utc(7, 30)));
        Assert.Null(QuietHours.Until(q, JobLanes.Analysis, Utc(22, 30)));
    }
}
