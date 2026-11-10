using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Une journée de la page Historique, à l'heure de l'appareil, et son temps d'écoute.</summary>
public sealed record HistoryDay(DateOnly Date, IReadOnlyList<HistoryEntryDto> Entries)
{
    public double Seconds => Entries.Sum(e => e.SecondsPlayed);
}

public static class HistoryDays
{
    /// <summary>Range les écoutes (déjà de la plus récente à la plus ancienne) par jour local, dans le même ordre.</summary>
    public static IReadOnlyList<HistoryDay> Group(IEnumerable<HistoryEntryDto> entries, TimeZoneInfo zone) =>
        entries.GroupBy(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.StartedAt, zone).DateTime))
            .Select(g => new HistoryDay(g.Key, g.ToList()))
            .ToList();
}
