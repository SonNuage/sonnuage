namespace SonNuage.Contracts;

public sealed record ArtistDto(Guid Id, string Name, int AlbumCount, int TrackCount, Guid? CoverAlbumId);

public sealed record AlbumDto(
    Guid Id,
    string Title,
    Guid ArtistId,
    string ArtistName,
    int? Year,
    int TrackCount,
    double DurationSeconds,
    bool HasCover,
    Guid? MusicBrainzReleaseId = null);

public sealed record TrackDto(
    Guid Id,
    string Title,
    Guid ArtistId,
    string ArtistName,
    Guid AlbumId,
    string AlbumTitle,
    int? TrackNumber,
    int? DiscNumber,
    double DurationSeconds,
    string Format,
    bool IsLossless,
    int? BitrateKbps,
    bool HasCover,
    bool IsFavorite,
    int? Rating = null);

/// <param name="IsFavorite">Album dans les favoris du compte (absent d'un serveur antérieur à 1.20).</param>
/// <param name="Rating">Note du compte, de 1 à 5 ; null sans note.</param>
public sealed record AlbumDetailDto(AlbumDto Album, IReadOnlyList<TrackDto> Tracks, bool IsFavorite = false, int? Rating = null);

public sealed record ArtistDetailDto(ArtistDto Artist, IReadOnlyList<AlbumDto> Albums, bool IsFavorite = false, int? Rating = null);

/// <summary>Note d'un titre, d'un album ou d'un artiste : de 1 à 5, 0 la retire.</summary>
public sealed record RatingRequest(int Value);

public sealed record SearchResultDto(
    IReadOnlyList<ArtistDto> Artists,
    IReadOnlyList<AlbumDto> Albums,
    IReadOnlyList<TrackDto> Tracks,
    string? Correction = null);

/// <summary>
/// Recherche par ambiance (« musique calme pour travailler », « rock énergique années 90 ») : les morceaux qui y
/// répondent, du plus au moins proche. Absente (204) quand la recherche n'est pas une ambiance.
/// </summary>
/// <param name="StationId">Radio thématique qui joue cette ambiance sans fin (<c>/api/radio/stations/{id}/next</c>).</param>
/// <param name="Terms">Expressions comprises (« calme », « travailler »), telles que saisies.</param>
public sealed record AmbianceResultDto(string StationId, IReadOnlyList<string> Terms, IReadOnlyList<TrackDto> Tracks);

/// <summary>Accueil. <see cref="Rediscover"/> et <see cref="NotYetPlayed"/> : absents d'un serveur antérieur à 1.7.</summary>
public sealed record HomeDto(
    IReadOnlyList<AlbumDto> RecentlyAdded,
    IReadOnlyList<TrackDto> RecentlyPlayed,
    IReadOnlyList<PlaylistDto> Playlists,
    IReadOnlyList<TrackDto>? Rediscover = null,
    IReadOnlyList<AlbumDto>? NotYetPlayed = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);

public sealed record PlaylistDto(
    Guid Id,
    string Name,
    string? Description,
    int TrackCount,
    double DurationSeconds,
    Guid? CoverAlbumId,
    DateTimeOffset UpdatedAt);

public sealed record PlaylistDetailDto(PlaylistDto Playlist, IReadOnlyList<PlaylistEntryDto> Entries);

public sealed record PlaylistEntryDto(Guid EntryId, int Position, TrackDto Track);

public sealed record CreatePlaylistRequest(string Name, string? Description, IReadOnlyList<Guid>? TrackIds);

public sealed record UpdatePlaylistRequest(string? Name, string? Description);

public sealed record AddPlaylistTracksRequest(IReadOnlyList<Guid> TrackIds, int? Position);

public sealed record MovePlaylistEntryRequest(int NewPosition);

public sealed record PlayEventRequest(Guid TrackId, double SecondsPlayed, bool Completed);

/// <param name="Build">Empreinte du client web servi : un onglet ouvert la compare pour proposer de recharger après un déploiement.</param>
/// <param name="Runtime">Environnement d'exécution du serveur (.NET, système), pour la page « À propos ».</param>
public sealed record ServerInfoDto(string Name, string Version, IReadOnlyList<QualityDto> Qualities, bool TranscodingAvailable, string? Build = null,
    string? Runtime = null);

public sealed record QualityDto(string Id, string Label, string Format, int? BitrateKbps);

/// <summary>
/// Lecture infinie : <see cref="Count"/> morceaux proches des <see cref="SeedTrackIds"/> (les derniers de la file),
/// hors <see cref="ExcludeTrackIds"/> (ce que la file contient déjà).
/// </summary>
public sealed record RadioRequest(IReadOnlyList<Guid> SeedTrackIds, IReadOnlyList<Guid> ExcludeTrackIds, int Count)
{
    public const int MaxCount = 20;
    public const int MaxIds = 500;
}

/// <summary>
/// Réglages de lecture qui suivent le compte sur tous ses appareils : fondu enchaîné (0 à 5 s, au dixième),
/// lecture infinie et nombre de morceaux choisis d'avance. La qualité et le pré-chargement restent propres à l'appareil.
/// </summary>
public sealed record PlaybackPreferences(double CrossfadeSeconds, bool InfinitePlay, int InfiniteAhead)
{
    public const double MaxCrossfadeSeconds = 5;
    public const int DefaultAhead = 3;
    public const int MaxAhead = 10;
    public static readonly PlaybackPreferences Default = new(0, false, DefaultAhead);

    /// <summary>Les mêmes bornes partout : serveur à l'enregistrement, appareils à la lecture.</summary>
    public PlaybackPreferences Clamped() =>
        new(Math.Round(Math.Clamp(double.IsFinite(CrossfadeSeconds) ? CrossfadeSeconds : 0, 0, MaxCrossfadeSeconds), 1),
            InfinitePlay, Math.Clamp(InfiniteAhead, 1, MaxAhead));
}
