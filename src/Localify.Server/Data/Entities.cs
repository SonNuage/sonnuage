namespace Localify.Server.Data;

public sealed class Artist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// <summary>Clé de regroupement (minuscules, sans accents ni ponctuation).</summary>
    public required string NormalizedName { get; set; }
    public string SortName { get; set; } = "";
    public List<Album> Albums { get; set; } = [];
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

    /// <summary>Métadonnées modifiées à la main : le scan ne les écrase plus.</summary>
    public bool MetadataLocked { get; set; }

    public Guid AlbumId { get; set; }
    public Album Album { get; set; } = null!;
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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? LastError { get; set; }
}
