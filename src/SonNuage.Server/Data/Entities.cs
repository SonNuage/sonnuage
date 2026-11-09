namespace SonNuage.Server.Data;

public sealed class Artist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// <summary>Clé de regroupement (minuscules, sans accents ni ponctuation).</summary>
    public required string NormalizedName { get; set; }
    public string SortName { get; set; } = "";
    /// <summary>Identifiant MusicBrainz, cherché par la découverte (nom identique, score 100).</summary>
    public string? MusicBrainzId { get; set; }
    /// <summary>Dernière recherche de l'identifiant MusicBrainz (trouvé ou non) : on ne la refait pas à chaque actualisation.</summary>
    public DateTimeOffset? MusicBrainzCheckedAt { get; set; }
    public List<Album> Albums { get; set; } = [];
    /// <summary>Morceaux dont il est l'artiste principal (y compris sur les albums d'autres artistes).</summary>
    public List<Track> Tracks { get; set; } = [];
}

/// <summary>Artiste invité sur un morceau (« OrelSan feat. Damso » : Damso). Voir <see cref="Library.ArtistCredit"/>.</summary>
public sealed class TrackGuest
{
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public Guid ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;
}

public sealed class Album
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public required string NormalizedTitle { get; set; }
    public Guid ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;
    public int? Year { get; set; }
    /// <summary>Chemin absolu d'une image du dossier, ou null si la pochette vient des tags (extraite dans le cache).</summary>
    public string? CoverPath { get; set; }
    public bool HasEmbeddedCover { get; set; }
    /// <summary>Change à chaque nouvelle pochette, sert d'ETag et invalide les miniatures.</summary>
    public int CoverVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Édition MusicBrainz de l'album : lue dans les tags, ou liée à la main dans l'admin (le scan ne l'écrase pas).</summary>
    public Guid? MusicBrainzReleaseId { get; set; }
    public List<Track> Tracks { get; set; } = [];
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Chemin absolu sur le serveur. Unique.</summary>
    public required string FilePath { get; set; }
    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }

    public required string Title { get; set; }
    public required string ArtistName { get; set; }
    public required string AlbumArtistName { get; set; }
    public required string AlbumTitle { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }

    public double DurationSeconds { get; set; }
    public int? BitrateKbps { get; set; }
    public int? SampleRate { get; set; }
    public required string Format { get; set; }
    public bool IsLossless { get; set; }

    /// <summary>Clés normalisées titre/artiste/album, séparées par des espaces, pour la recherche.</summary>
    public string SearchKey { get; set; } = "";

    /// <summary>Enregistrement MusicBrainz (tag « MusicBrainz Track Id » de Picard), ou reconnu par AcoustID. Non nul = identifié.</summary>
    public Guid? MusicBrainzRecordingId { get; set; }
    /// <summary>Édition MusicBrainz d'où vient le morceau.</summary>
    public Guid? MusicBrainzReleaseId { get; set; }

    /// <summary>Métadonnées modifiées à la main : le scan ne les écrase plus.</summary>
    public bool MetadataLocked { get; set; }

    public Guid AlbumId { get; set; }
    public Album Album { get; set; } = null!;
    /// <summary>Artiste principal du crédit (<see cref="ArtistName"/> sans ses invités).</summary>
    public Guid ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public string PasswordHash { get; set; } = "";
    public required string Role { get; set; }
    /// <summary>Changé au changement de mot de passe : invalide tous les jetons média émis.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Langue préférée de l'interface (« fr », « en »…), suivie sur tous ses appareils. Null = celle de l'appareil.</summary>
    public string? Language { get; set; }
    /// <summary>Réglages de lecture suivis sur tous ses appareils (voir <see cref="Contracts.PlaybackPreferences"/>).</summary>
    public double CrossfadeSeconds { get; set; }
    public bool InfinitePlay { get; set; }
    public int InfiniteAhead { get; set; } = Contracts.PlaybackPreferences.DefaultAhead;
    /// <summary>Contributeur : demandes de téléchargement permises sur 24 heures glissantes.</summary>
    public int RequestsPerDay { get; set; } = Contracts.DownloadRequestLimits.DefaultPerDay;
    /// <summary>Contributeur : ses demandes partent au téléchargement sans attendre un admin.</summary>
    public bool AutoApproveRequests { get; set; }
    /// <summary>
    /// Dernière connexion : à la saisie du mot de passe et à chaque renouvellement de session (toutes les quelques
    /// minutes tant que l'appli sert), donc aussi pour une appli restée connectée des semaines.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>Appareil de cette dernière connexion (nom donné à la connexion, ex. « samsung SM-G990B2 »).</summary>
    public string? LastSeenDevice { get; set; }
    /// <summary>Mot de passe des applis Subsonic, chiffré (voir <see cref="Subsonic.SubsonicPasswords"/>). Null = désactivé.</summary>
    public string? SubsonicPassword { get; set; }
    /// <summary>Empreinte du mot de passe Subsonic, pour la clé d'API OpenSubsonic (<c>apiKey</c>) qui ne donne pas le nom.</summary>
    public string? SubsonicKeyHash { get; set; }
    public DateTimeOffset? SubsonicPasswordCreatedAt { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public required string TokenHash { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PlaylistEntry> Entries { get; set; } = [];
}

public sealed class PlaylistEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public int Position { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Favorite
{
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// File de lecture d'un compte, enregistrée par une appli Subsonic pour être reprise sur un autre appareil
/// (même titre, même seconde). Une par compte.
/// </summary>
public sealed class SavedQueue
{
    public const int MaxEntries = 2000;
    public const int MaxClientLength = 80;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid? CurrentTrackId { get; set; }
    public long PositionMs { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public List<SavedQueueEntry> Entries { get; set; } = [];
}

public sealed class SavedQueueEntry
{
    public Guid UserId { get; set; }
    public int Position { get; set; }
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
}

public sealed class PlayEvent
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public DateTimeOffset PlayedAt { get; set; } = DateTimeOffset.UtcNow;
    public double SecondsPlayed { get; set; }
    public bool Completed { get; set; }
}

public enum YoutubeItemStatus
{
    Queued,
    Downloading,
    Processing,
    Done,
    Failed,
    /// <summary>Déjà publiée au moment de l'abonnement : pas importée, sauf demande explicite.</summary>
    Skipped,
    /// <summary>Fichier téléchargé, en attente des métadonnées (étape suivante : Processing).</summary>
    Downloaded,
}

/// <summary>Une vidéo YouTube demandée par l'admin ou trouvée par un abonnement, et ce qu'elle est devenue.</summary>
public sealed class YoutubeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string VideoId { get; set; }
    public required string Title { get; set; }
    public string? Channel { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }

    public Guid? SubscriptionId { get; set; }
    public YoutubeSubscription? Subscription { get; set; }
    /// <summary>Titre de la playlist d'origine quand elle représente un album (aide la recherche de métadonnées).</summary>
    public string? AlbumHint { get; set; }

    public YoutubeItemStatus Status { get; set; } = YoutubeItemStatus.Queued;
    public string? Error { get; set; }
    public int Attempts { get; set; }

    public Guid? TrackId { get; set; }
    public Track? Track { get; set; }
    /// <summary>Métadonnées reconnues avec une confiance insuffisante : l'admin doit confirmer.</summary>
    public bool NeedsReview { get; set; }
    public double? MatchConfidence { get; set; }
    /// <summary>youtube-music, acoustid, musicbrainz, titre.</summary>
    public string? MatchSource { get; set; }
    /// <summary>Candidats MusicBrainz (JSON) proposés à l'admin.</summary>
    public string? CandidatesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Playlist ou chaîne suivie : ses nouvelles vidéos sont importées à chaque synchronisation.</summary>
public sealed class YoutubeSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Url { get; set; }
    public required string Title { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>La playlist est un album (ex. album YouTube Music) : son titre sert d'indice d'album.</summary>
    public bool IsAlbum { get; set; }
    /// <summary>À la première lecture, importer aussi les vidéos déjà publiées (sinon elles sont seulement notées, ignorées).</summary>
    public bool ImportExisting { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? LastError { get; set; }
}

public enum ApiKeyKind
{
    /// <summary>Extension de navigateur : limitée à l'ajout d'imports YouTube (voir ApiKeyAuthentication).</summary>
    BrowserExtension,
}

/// <summary>Clé d'API créée par un admin. Seule son empreinte SHA-256 est conservée.</summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ApiKeyKind Kind { get; set; } = ApiKeyKind.BrowserExtension;
    public required string KeyHash { get; set; }
    /// <summary>Début de la clé, affiché pour la reconnaître.</summary>
    public required string Prefix { get; set; }
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

public enum JobStatus
{
    /// <summary>Attend la fin de la tâche dont elle dépend.</summary>
    Pending,
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>Suite de tâches créées ensemble (un import YouTube, un rescan AcoustID…), affichée comme un tout.</summary>
public sealed class JobSuite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Job> Jobs { get; set; } = [];
}

/// <summary>Une tâche : exécutée par un consommateur de sa file (<see cref="Lane"/>), dont la capacité est bornée.</summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SuiteId { get; set; }
    public JobSuite Suite { get; set; } = null!;
    /// <summary>Tâche à terminer avec succès avant celle-ci. Sa sortie devient l'entrée de celle-ci.</summary>
    public Guid? DependsOnId { get; set; }
    /// <summary>Objet traité (import YouTube, morceau…), pour retrouver ses tâches.</summary>
    public Guid? SubjectId { get; set; }
    public required string Kind { get; set; }
    public required string Lane { get; set; }
    public JobStatus Status { get; set; }
    public required string Title { get; set; }
    public string? Payload { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 1;
    public double? Progress { get; set; }
    /// <summary>Plus grand = servi d'abord (les imports passent devant un rescan de la bibliothèque).</summary>
    public int Priority { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? WorkerId { get; set; }
    public bool CancelRequested { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class JobLog
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Level { get; set; }
    public required string Message { get; set; }
}

/// <summary>Rapport de plantage reçu d'une appli (l'identifiant vient de l'appli : un renvoi ne fait pas de doublon).</summary>
public sealed class CrashReport
{
    public const int MaxFieldLength = 200;
    public const int MaxHeadlineLength = 300;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string App { get; set; }
    public required string AppVersion { get; set; }
    public required string Device { get; set; }
    public required string Os { get; set; }
    public required string Source { get; set; }
    /// <summary>Première ligne du rapport (type et message de l'exception), pour la liste.</summary>
    public required string Headline { get; set; }
    public required string Report { get; set; }
}

public enum DownloadRequestDecision
{
    Requested,
    Accepted,
    Denied,
}

/// <summary>
/// Demande de téléchargement d'un contributeur. Une fois acceptée, elle suit l'import YouTube lié : son état
/// « en cours », « terminée » ou « échouée » se lit sur <see cref="YoutubeItem"/>.
/// </summary>
public sealed class DownloadRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public required string VideoId { get; set; }
    public required string Title { get; set; }
    public string? Channel { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>Mot de l'auteur pour l'admin.</summary>
    public string? Note { get; set; }
    public DownloadRequestDecision Decision { get; set; }
    public string? DenyReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>Admin qui a tranché (nom gardé même s'il est supprimé) ; null si approuvée automatiquement.</summary>
    public string? DecidedBy { get; set; }
    public bool AutoApproved { get; set; }
    public Guid? YoutubeItemId { get; set; }
    public YoutubeItem? YoutubeItem { get; set; }
}

/// <summary>Artiste à découvrir, hors de la bibliothèque : proche d'artistes qu'on a (ListenBrainz). Voir Discovery/.</summary>
public sealed class DiscoveryArtist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string MusicBrainzId { get; set; }
    public required string Name { get; set; }
    public string? Comment { get; set; }
    /// <summary>Proximité cumulée sur les artistes de la bibliothèque qui y mènent (pour l'ordre d'affichage).</summary>
    public double Score { get; set; }
    /// <summary>Artistes de la bibliothèque qui y mènent, un par ligne, du plus proche au moins proche.</summary>
    public string BecauseOf { get; set; } = "";
    /// <summary>Proposé pour la première fois (les plus récents s'affichent en haut ; retiré au bout de 7 jours).</summary>
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Titres phares lus (null : pas encore).</summary>
    public DateTimeOffset? TracksFetchedAt { get; set; }
    public List<DiscoveryTrack> Tracks { get; set; } = [];
}

/// <summary>Titre phare d'un artiste à découvrir, et la vidéo YouTube qui lui correspond (recherche par correspondance).</summary>
public sealed class DiscoveryTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArtistId { get; set; }
    public DiscoveryArtist Artist { get; set; } = null!;
    public int Rank { get; set; }
    public required string Title { get; set; }
    public string? RecordingMbid { get; set; }
    public string? ReleaseName { get; set; }
    public double? DurationSeconds { get; set; }
    public string? VideoId { get; set; }
    public string? VideoTitle { get; set; }
    public string? Channel { get; set; }
    public int? MatchScore { get; set; }
    /// <summary>Recherche YouTube faite (trouvée ou non) : on ne la refait pas à chaque actualisation.</summary>
    public DateTimeOffset? MatchedAt { get; set; }
}
