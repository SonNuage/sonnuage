namespace SonNuage.UI.Services;

/// <summary>
/// Temps écoulé dit simplement : « à l'instant », « il y a 5 min », « il y a 3 h », « il y a 2 jours ».
/// Au-delà d'un mois, rien (on affiche la date).
/// </summary>
public static class RelativeTime
{
    public static (string Key, int Count)? Of(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now - value;
        if (elapsed < TimeSpan.FromMinutes(1)) return ("format.ago.now", 0);
        if (elapsed < TimeSpan.FromHours(1)) return ("format.ago.minutes", (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1)) return ("format.ago.hours", (int)elapsed.TotalHours);
        if (elapsed < TimeSpan.FromDays(30)) return ("format.ago.days", (int)elapsed.TotalDays);
        return null;
    }
}
