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
