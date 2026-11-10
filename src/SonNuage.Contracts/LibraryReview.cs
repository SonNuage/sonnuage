namespace SonNuage.Contracts;

/// <summary>Un champ que la proposition changerait : valeur actuelle (null si vide) et valeur proposée.</summary>
public sealed record ProposalChangeDto(string Field, string? From, string To);

/// <summary>
/// Correction proposée par un rescan AcoustID. Title, ArtistName, AlbumTitle : tags actuels du morceau ; Proposed* :
/// résultat MusicBrainz. Changes : ce que l'acceptation changerait aujourd'hui. Status : « Pending », « Accepted »,
/// « Rejected » ou « Superseded ». Writable : fichier d'uploads/, dont les tags seront réécrits.
/// </summary>
public sealed record RescanProposalDto(
    Guid Id,
    Guid TrackId,
    Guid? SuiteId,
    string? SuiteTitle,
    string Status,
    double Score,
    string Title,
    string ArtistName,
    string AlbumTitle,
    string ProposedTitle,
    string ProposedArtist,
    string? ProposedAlbum,
    Guid? RecordingId,
    Guid? ReleaseId,
    IReadOnlyList<ProposalChangeDto> Changes,
    bool Locked,
    bool Writable,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

/// <summary>Rescan qui a laissé des propositions, et combien attendent encore une décision.</summary>
public sealed record ProposalSuiteDto(Guid SuiteId, string Title, DateTimeOffset CreatedAt, int Pending);

/// <summary>Propositions à accepter ou refuser ensemble (au plus quelques centaines par envoi).</summary>
public sealed record DecideProposalsRequest(IReadOnlyList<Guid> Ids);

/// <summary>Done : propositions acceptées (ou refusées). Skipped : plus applicables (morceau modifié à la main, déjà à jour).</summary>
public sealed record DecideProposalsResultDto(int Done, int Skipped);

/// <summary>
/// Une copie d'un morceau en double. InUploads : fichier d'uploads/ (supprimable) ; sinon bibliothèque en lecture seule
/// (la copie écartée est seulement masquée). Plays, Favorites, Playlists, Ratings : écoutes, comptes qui l'ont en
/// favori, playlists qui la contiennent, notes, tous comptes confondus.
/// </summary>
public sealed record DuplicateCopyDto(
    Guid TrackId,
    string Title,
    string ArtistName,
    string AlbumTitle,
    string RelativePath,
    string Format,
    int? BitrateKbps,
    int? SampleRate,
    bool IsLossless,
    double DurationSeconds,
    long FileSize,
    bool InUploads,
    int Plays,
    int Favorites,
    int Playlists,
    int Ratings);

/// <summary>
/// Copies d'un même enregistrement. Reason : « recording » (même identifiant d'enregistrement MusicBrainz) ou
/// « fingerprint » (empreintes acoustiques très proches). SuggestedKeepId : la meilleure qualité.
/// </summary>
public sealed record DuplicateGroupDto(string Reason, Guid? RecordingId, Guid SuggestedKeepId, IReadOnlyList<DuplicateCopyDto> Copies);

/// <summary>Doublons de la bibliothèque, et où en est le calcul des empreintes (Fingerprinted sur Tracks).</summary>
public sealed record DuplicatesDto(int Tracks, int Fingerprinted, bool FingerprintAvailable, IReadOnlyList<DuplicateGroupDto> Groups);

/// <summary>Garde KeepId et écarte RemoveIds : favoris, notes, écoutes et playlists passent sur la copie gardée.</summary>
public sealed record ResolveDuplicatesRequest(Guid KeepId, IReadOnlyList<Guid> RemoveIds);

/// <summary>Deleted : fichiers d'uploads/ supprimés ; Hidden : fichiers en lecture seule masqués. AuditSuiteId : journal (volet Tâches).</summary>
public sealed record ResolveDuplicatesResultDto(int Removed, IReadOnlyList<string> Deleted, IReadOnlyList<string> Hidden, Guid AuditSuiteId);
