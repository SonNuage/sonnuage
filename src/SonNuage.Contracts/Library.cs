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
    bool HasCover);

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
    bool IsFavorite);

public sealed record AlbumDetailDto(AlbumDto Album, IReadOnlyList<TrackDto> Tracks);

public sealed record ArtistDetailDto(ArtistDto Artist, IReadOnlyList<AlbumDto> Albums);

public sealed record SearchResultDto(
    IReadOnlyList<ArtistDto> Artists,
    IReadOnlyList<AlbumDto> Albums,
    IReadOnlyList<TrackDto> Tracks,
    string? Correction = null);

public sealed record HomeDto(
    IReadOnlyList<AlbumDto> RecentlyAdded,
    IReadOnlyList<TrackDto> RecentlyPlayed,
    IReadOnlyList<PlaylistDto> Playlists);

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
