namespace SonNuage.Contracts;

public sealed record LibraryStatsDto(
    int Artists,
    int Albums,
    int Tracks,
    long TotalBytes,
    double TotalDurationSeconds,
    long TranscodeCacheBytes,
    ScanStatusDto Scan,
    string SearchEngine = "database");

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

/// <summary>Lie un album à son édition MusicBrainz : identifiant ou lien musicbrainz.org/release/…, vide pour délier.</summary>
public sealed record LinkAlbumReleaseRequest(string? Release);

/// <summary>Identifiant MusicBrainz d'un artiste : identifiant ou lien musicbrainz.org/artist/…, vide pour l'effacer.</summary>
public sealed record LinkArtistRequest(string? Artist);

public sealed record StartUploadRequest(string FileName, long Size, string? ArtistHint, string? AlbumHint);

public sealed record UploadSessionDto(Guid Id, string FileName, long Size, long Received, int ChunkSize);

public sealed record CreateUserRequest(string Username, string Password, string Role);

public sealed record UpdateUserRequest(string? Password, string? Role, int? RequestsPerDay = null, bool? AutoApproveRequests = null);
