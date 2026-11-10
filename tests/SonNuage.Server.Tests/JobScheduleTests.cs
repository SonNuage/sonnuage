using SonNuage.Server.Jobs;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class JobScheduleTests
{
    private static readonly TimeZoneInfo Paris = QuietHours.Zone("Europe/Paris")!;

    private static ScheduleTimes Sundays(int hour, int minute = 0) => new([DayOfWeek.Sunday], new TimeOnly(hour, minute), Paris);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Sunday_at_three_in_paris_is_two_utc_in_winter()
    {
        var s = Sundays(3);
        // Mardi 10 novembre : le dernier passage était dimanche 8 à 3 h (UTC+1), le prochain dimanche 15.
        Assert.Equal(Utc(11, 8, 2), JobScheduling.LastSlot(s, Utc(11, 10, 12)));
        Assert.Equal(Utc(11, 15, 2), JobScheduling.NextSlot(s, Utc(11, 10, 12)));
    }

    [Fact]
    public void The_slot_is_reached_at_its_exact_time()
    {
        var s = Sundays(3);
        Assert.Equal(Utc(11, 8, 2), JobScheduling.LastSlot(s, Utc(11, 8, 2)));
        Assert.Equal(Utc(11, 15, 2), JobScheduling.NextSlot(s, Utc(11, 8, 2)));
        // Une minute avant : le passage de la semaine précédente, et le prochain dans une minute.
        Assert.Equal(Utc(11, 1, 2), JobScheduling.LastSlot(s, Utc(11, 8, 1, 59)));
        Assert.Equal(Utc(11, 8, 2), JobScheduling.NextSlot(s, Utc(11, 8, 1, 59)));
    }

    [Fact]
    public void Several_days_of_the_week_follow_each_other()
    {
        var s = new ScheduleTimes([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(22, 30), Paris);
        // Mardi 10 novembre midi : lundi 9 à 22 h 30 (21 h 30 UTC), puis jeudi 12.
        Assert.Equal(Utc(11, 9, 21, 30), JobScheduling.LastSlot(s, Utc(11, 10, 12)));
        Assert.Equal(Utc(11, 12, 21, 30), JobScheduling.NextSlot(s, Utc(11, 10, 12)));
    }

    [Fact]
    public void Summer_time_moves_the_utc_hour()
    {
        var s = Sundays(3);
        // 3 h à Paris : 1 h UTC le 18 octobre (UTC+2), 2 h UTC le 25 (retour à l'heure d'hiver la nuit même).
        Assert.Equal(Utc(10, 18, 1), JobScheduling.LastSlot(s, Utc(10, 20, 0)));
        Assert.Equal(Utc(10, 25, 2), JobScheduling.NextSlot(s, Utc(10, 20, 0)));
    }

    [Fact]
    public void A_time_skipped_by_the_spring_change_runs_an_hour_later()
    {
        // 2 h 30 n'existe pas le 29 mars 2026 à Paris (2 h → 3 h) : passage à 3 h 30 heure d'été, soit 1 h 30 UTC.
        var s = Sundays(2, 30);
        Assert.Equal(Utc(3, 29, 1, 30), JobScheduling.NextSlot(s, Utc(3, 28, 12)));
        Assert.Equal(Utc(3, 29, 1, 30), JobScheduling.LastSlot(s, Utc(3, 29, 2)));
    }

    [Fact]
    public void No_day_means_never()
    {
        var s = new ScheduleTimes([], new TimeOnly(3, 0), Paris);
        Assert.Null(JobScheduling.LastSlot(s, Utc(11, 10, 12)));
        Assert.Null(JobScheduling.NextSlot(s, Utc(11, 10, 12)));
    }

    [Fact]
    public void A_missed_slot_is_due_once()
    {
        var s = Sundays(3);
        // Serveur éteint depuis le dimanche 1er novembre (dernier passage) : au retour, un seul passage dû.
        var lastRun = Utc(11, 1, 2);
        Assert.True(JobScheduling.IsDue(s, lastRun, Utc(11, 10, 12)));
        // Lancée au retour : plus rien de dû avant dimanche prochain.
        Assert.False(JobScheduling.IsDue(s, Utc(11, 10, 12), Utc(11, 10, 12, 1)));
        Assert.False(JobScheduling.IsDue(s, Utc(11, 10, 12), Utc(11, 15, 1, 59)));
        Assert.True(JobScheduling.IsDue(s, Utc(11, 10, 12), Utc(11, 15, 2)));
    }

    [Fact]
    public void A_schedule_created_after_its_slot_waits_for_the_next_one()
    {
        // Créée mardi : le dimanche passé ne compte pas.
        Assert.False(JobScheduling.IsDue(Sundays(3), Utc(11, 10, 12), Utc(11, 10, 12, 5)));
    }
}
