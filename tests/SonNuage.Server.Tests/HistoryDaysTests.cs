using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Page Historique : les écoutes sont rangées par jour, à l'heure de l'appareil.</summary>
public sealed class HistoryDaysTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
    private static readonly TrackDto Track = new(Guid.NewGuid(), "Titre", Guid.NewGuid(), "Artiste", Guid.NewGuid(), "Album", 1, 1, 200, "mp3", false, null,
        false, false);

    private static HistoryEntryDto At(long id, DateTimeOffset at) => new(id, Track, at, 100, true, "web");

    [Fact]
    public void Listens_are_grouped_by_local_day_latest_first()
    {
        var entries = new[]
        {
            At(4, new DateTimeOffset(2026, 11, 10, 0, 30, 0, TimeSpan.Zero)),   // 10 nov., 1 h 30 à l'heure locale
            At(3, new DateTimeOffset(2026, 11, 9, 23, 30, 0, TimeSpan.Zero)),   // 10 nov., 0 h 30 à l'heure locale
            At(2, new DateTimeOffset(2026, 11, 9, 22, 30, 0, TimeSpan.Zero)),   // 9 nov., 23 h 30 à l'heure locale
            At(1, new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)),
        };

        var days = HistoryDays.Group(entries, Paris);

        Assert.Equal([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 2)], days.Select(d => d.Date));
        Assert.Equal([4L, 3L], days[0].Entries.Select(e => e.Id));
        Assert.Equal(200, days[0].Seconds);
    }

    [Fact]
    public void A_page_loaded_later_joins_the_day_already_shown()
    {
        var first = new[] { At(2, new DateTimeOffset(2026, 11, 9, 20, 0, 0, TimeSpan.Zero)) };
        var more = new[] { At(1, new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)) };

        var days = HistoryDays.Group(first.Concat(more), Paris);

        Assert.Equal([2L, 1L], Assert.Single(days).Entries.Select(e => e.Id));
    }
}
