namespace SonNuage.Contracts;

/// <summary>
/// Quand une lecture compte. Les mêmes règles partout : lecteurs (web, applis) avant d'envoyer, serveur avant de
/// transmettre à ListenBrainz. Une durée inconnue (0) vaut un long morceau : seul le seuil fixe s'applique.
/// </summary>
public static class ListenRules
{
    /// <summary>Entrée dans l'historique : 30 secondes, ou la moitié d'un morceau plus court.</summary>
    public const double PlaySeconds = 30;

    /// <summary>Écoute au sens de ListenBrainz : 4 minutes, ou la moitié du morceau si c'est moins.</summary>
    public const double ListenBrainzSeconds = 240;

    public static bool CountsAsPlay(double secondsPlayed, double durationSeconds) => Reached(secondsPlayed, durationSeconds, PlaySeconds);

    public static bool CountsForListenBrainz(double secondsPlayed, double durationSeconds) =>
        Reached(secondsPlayed, durationSeconds, ListenBrainzSeconds);

    private static bool Reached(double seconds, double duration, double cap) =>
        double.IsFinite(seconds) && seconds > 0 && seconds >= (duration > 0 ? Math.Min(cap, duration / 2) : cap);
}

/// <summary>Une écoute de l'historique : début, durée écoutée, jusqu'au bout ou passée, et l'appli.</summary>
public sealed record HistoryEntryDto(long Id, TrackDto Track, DateTimeOffset StartedAt, double SecondsPlayed, bool Completed, string? Client);

/// <summary>
/// Statistiques de l'historique : temps d'écoute sur 7 et 30 jours glissants, puis écoutes, artistes et titres les
/// plus écoutés sur la période demandée (<see cref="Days"/> jours, 0 = depuis le début).
/// </summary>
public sealed record HistoryStatsDto(int Days, double WeekSeconds, double MonthSeconds, int Plays, IReadOnlyList<HistoryTopArtistDto> TopArtists,
    IReadOnlyList<HistoryTopTrackDto> TopTracks);

public sealed record HistoryTopArtistDto(ArtistDto Artist, int Plays);

public sealed record HistoryTopTrackDto(TrackDto Track, int Plays);
