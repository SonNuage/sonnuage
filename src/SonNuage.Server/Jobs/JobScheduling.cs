namespace SonNuage.Server.Jobs;

/// <summary>Horaire d'une tâche planifiée : jours de la semaine et heure locale du fuseau <see cref="Zone"/>.</summary>
public sealed record ScheduleTimes(IReadOnlyCollection<DayOfWeek> Days, TimeOnly At, TimeZoneInfo Zone);

/// <summary>
/// Calcul des passages d'une tâche planifiée. Une heure sautée par le passage à l'heure d'été est décalée d'autant
/// (2 h 30 devient 3 h 30) ; une heure vécue deux fois à l'automne compte une seule fois (la seconde).
/// </summary>
public static class JobScheduling
{
    /// <summary>Dernier passage atteint à cet instant (ou à l'instant même), null sans jour choisi.</summary>
    public static DateTimeOffset? LastSlot(ScheduleTimes s, DateTimeOffset now)
    {
        var today = TimeZoneInfo.ConvertTime(now, s.Zone).Date;
        // Huit jours : le jour même (horaire pas encore atteint) revient une semaine plus tôt.
        for (var d = 0; d <= 7; d++)
            if (Slot(s, today.AddDays(-d)) is { } slot && slot <= now) return slot;
        return null;
    }

    /// <summary>Prochain passage strictement après cet instant, null sans jour choisi.</summary>
    public static DateTimeOffset? NextSlot(ScheduleTimes s, DateTimeOffset now)
    {
        var today = TimeZoneInfo.ConvertTime(now, s.Zone).Date;
        for (var d = 0; d <= 7; d++)
            if (Slot(s, today.AddDays(d)) is { } slot && slot > now) return slot;
        return null;
    }

    /// <summary>
    /// Un passage a eu lieu depuis <paramref name="since"/> (dernier lancement, ou création / modification de la
    /// planification) : la tâche est due. Plusieurs passages manqués (serveur éteint) ne la rendent due qu'une fois.
    /// </summary>
    public static bool IsDue(ScheduleTimes s, DateTimeOffset since, DateTimeOffset now) => LastSlot(s, now) is { } slot && slot > since;

    private static DateTimeOffset? Slot(ScheduleTimes s, DateTime date)
    {
        if (!s.Days.Contains(date.DayOfWeek)) return null;
        var local = date + s.At.ToTimeSpan();
        // Heure inexistante (printemps) : GetUtcOffset donne le décalage d'hiver, soit une heure plus tard en heure d'été.
        return new DateTimeOffset(local, s.Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
