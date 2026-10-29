namespace Localify.Contracts;

/// <summary>Versions des outils trouvés (null = absent).</summary>
public sealed record ToolsStatusDto(string? YtDlp, string? Deno, string? Fpcalc);

public sealed record YoutubeStatusDto(
    ToolsStatusDto Tools,
    bool HasCookies,
    DateTimeOffset? CookiesUpdatedAt,
    // YouTube a refusé (429, « confirmez que vous n'êtes pas un robot ») : la file reprend seule à HoldUntil.
    DateTimeOffset? HoldUntil,
    string? HoldReason,
    int Queued,
    int Failed,
    int NeedsReview,
    int Done,
    YoutubeProgressDto? Current);

/// <summary>Contenu d'un export cookies.txt (format Netscape) d'un navigateur connecté à YouTube.</summary>
public sealed record YoutubeCookiesRequest(string? Content);

public sealed record YoutubeProgressDto(Guid ItemId, string Title, string Stage, double? Percent);

public sealed record YoutubeSettingsDto(
    int SyncIntervalHours,
    // « original » (opus/AAC tel que publié, sans perte supplémentaire) ou « mp3 ».
    string AudioFormat,
    string? AcoustIdKey,
    // Au-dessus, les métadonnées trouvées sont appliquées sans demander ; en dessous, l'import attend une vérification.
    double AutoAcceptThreshold);

public sealed record YoutubeVideoDto(
    string VideoId,
    string Title,
    string? Channel,
    double? DurationSeconds,
    string? ThumbnailUrl,
    // État de l'import si la vidéo est déjà connue (Queued, Done, Failed…), sinon null.
    string? ImportStatus);

public sealed record YoutubeListingDto(string Kind, string Title, string? Channel, string Url, IReadOnlyList<YoutubeVideoDto> Entries);

public sealed record YoutubeImportRequest(IReadOnlyList<YoutubeVideoDto> Videos, string? AlbumHint);

public sealed record YoutubeImportResultDto(int Queued, int AlreadyKnown);

public sealed record YoutubeItemDto(
    Guid Id,
    string VideoId,
    string Title,
    string? Channel,
    double? DurationSeconds,
    string? ThumbnailUrl,
    string Status,
    string? Error,
    int Attempts,
    Guid? TrackId,
    string? TrackTitle,
    string? TrackArtist,
    string? TrackAlbum,
    Guid? AlbumId,
    bool NeedsReview,
    double? MatchConfidence,
    string? MatchSource,
    string? SubscriptionTitle,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Métadonnées proposées pour un import (MusicBrainz, YouTube Music ou titre de la vidéo).</summary>
public sealed record MetadataCandidateDto(
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    string? Genre,
    double Score,
    string Source,
    string? RecordingId = null,
    string? ReleaseId = null,
    string? ReleaseGroupId = null);

public sealed record YoutubeSubscriptionDto(
    Guid Id,
    string Url,
    string Title,
    bool Enabled,
    bool IsAlbum,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSyncedAt,
    string? LastError,
    int Items,
    int Done);

public sealed record CreateSubscriptionRequest(string Url, bool IsAlbum, bool ImportExisting);

public sealed record UpdateSubscriptionRequest(bool? Enabled, bool? IsAlbum);

/// <summary>Tableau de bord de l'admin : volumes par provenance, historique des ajouts, disque.</summary>
public sealed record DashboardDto(
    int Artists,
    int Albums,
    int Tracks,
    int Playlists,
    int Users,
    IReadOnlyList<SourceTotalDto> Sources,
    IReadOnlyList<ImportBucketDto> History,
    string HistoryRange,
    StorageDto? Storage);

/// <summary>Provenance : library (dossiers de musique), upload (envois de l'admin), youtube.</summary>
public sealed record SourceTotalDto(string Source, int Tracks, double DurationSeconds, long Bytes);

public sealed record ImportBucketDto(DateTimeOffset Start, int Library, int Upload, int Youtube);

/// <summary>Disque qui reçoit les envois et imports. Les tranches incluent « other » (autres fichiers) et « free ».</summary>
public sealed record StorageDto(string Disk, long TotalBytes, long FreeBytes, IReadOnlyList<StorageSliceDto> Slices);

public sealed record StorageSliceDto(string Key, long Bytes);
