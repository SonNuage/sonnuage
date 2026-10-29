namespace Localify.Contracts;

public sealed record LibraryStatsDto(
    int Artists,
    int Albums,
    int Tracks,
    long TotalBytes,
    double TotalDurationSeconds,
    long TranscodeCacheBytes,
    ScanStatusDto Scan);

public sealed record ScanStatusDto(
    bool Running,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastFinishedAt,
    int FilesSeen,
    int Added,
    int Updated,
    int Removed,
    string? LastError);

public sealed record TrackMetadataDto(
    Guid Id,
    string RelativePath,
    string Title,
    string ArtistName,
    string AlbumTitle,
    string AlbumArtistName,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    string? Genre,
    bool MetadataLocked,
    // Fichier envoyé via l'admin, donc supprimable (la bibliothèque principale est en lecture seule).
    bool Deletable);

public sealed record UpdateTrackMetadataRequest(
    string? Title,
    string? ArtistName,
    string? AlbumTitle,
    string? AlbumArtistName,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    string? Genre);

public sealed record UpdateAlbumRequest(string? Title, string? ArtistName, int? Year);

public sealed record StartUploadRequest(string FileName, long Size, string? ArtistHint, string? AlbumHint);

public sealed record UploadSessionDto(Guid Id, string FileName, long Size, long Received, int ChunkSize);

public sealed record CreateUserRequest(string Username, string Password, string Role);

public sealed record UpdateUserRequest(string? Password, string? Role);
