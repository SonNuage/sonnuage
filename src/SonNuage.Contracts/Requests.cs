namespace SonNuage.Contracts;

public static class DownloadRequestLimits
{
    /// <summary>Demandes par contributeur sur 24 heures glissantes, tant que l'admin n'a rien réglé.</summary>
    public const int DefaultPerDay = 100;
    public const int MaxPerDay = 10_000;
}

/// <summary>Étapes d'une demande : demandée → acceptée ou refusée → en cours → terminée (ou échouée).</summary>
public static class DownloadRequestStatus
{
    public const string Requested = "requested";
    public const string Accepted = "accepted";
    public const string Denied = "denied";
    public const string InProgress = "inProgress";
    public const string Done = "done";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Requested, Accepted, Denied, InProgress, Done, Failed];
}

/// <summary>Video : identifiant de la vidéo YouTube ou son adresse (youtube.com, youtu.be, music.youtube.com).</summary>
public sealed record CreateDownloadRequest(string Video, string? Title, string? Channel, double? DurationSeconds, string? Note);

public sealed record DenyDownloadRequest(string? Reason);

public sealed record DownloadRequestDto(
    Guid Id,
    string VideoId,
    string Title,
    string? Channel,
    double? DurationSeconds,
    string ThumbnailUrl,
    string? Note,
    // Voir DownloadRequestStatus.
    string Status,
    string? DenyReason,
    Guid UserId,
    string Username,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    // Admin qui a tranché ; null pour une demande approuvée automatiquement.
    string? DecidedBy,
    bool AutoApproved,
    // Morceau ajouté à la bibliothèque, une fois le téléchargement terminé.
    Guid? TrackId);

/// <summary>Limit : null pour un administrateur (pas de quota).</summary>
public sealed record RequestQuotaDto(int Used, int? Limit, bool AutoApproved);

/// <summary>Un résultat YouTube noté sur 100 pour « artiste + titre ».</summary>
/// <summary>Demande d'un album entier : ses titres sont cherchés sur YouTube puis demandés un par un, en tâche de fond.</summary>
public sealed record CreateAlbumRequest(string Artist, string Album);

public sealed record MatchCandidateDto(YoutubeVideoDto Video, int Score);

/// <summary>
/// Recherche par correspondance. <paramref name="Sure"/> : le premier candidat est assez sûr pour être retenu
/// d'office ; sinon, l'utilisateur choisit parmi les candidats. La durée attendue vient de la requête ou de
/// MusicBrainz (<paramref name="DurationFromMusicBrainz"/>).
/// </summary>
public sealed record MatchResultDto(string Artist, string Title, double? DurationSeconds, bool DurationFromMusicBrainz, bool Sure,
    IReadOnlyList<MatchCandidateDto> Candidates);
