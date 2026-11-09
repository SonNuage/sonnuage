namespace SonNuage.Contracts;

/// <summary>Titre phare d'un artiste à découvrir, retrouvé sur YouTube quand c'est possible (sinon <paramref name="VideoId"/> nul).</summary>
/// <param name="PreviewReady">Extrait déjà téléchargé : il se lit tout de suite.</param>
/// <param name="ImportStatus">État de l'import YouTube de cette vidéo, si elle est déjà connue du serveur.</param>
public sealed record DiscoveryTrackDto(Guid Id, string Title, double? DurationSeconds, string? ReleaseName, string? VideoId, string? VideoTitle,
    string? Channel, string? ThumbnailUrl, int? MatchScore, bool PreviewReady, string? ImportStatus);

/// <summary>Artiste proche de ceux de la bibliothèque. <paramref name="BecauseOf"/> : les artistes de la bibliothèque qui y mènent.</summary>
public sealed record DiscoveryArtistDto(Guid Id, string Name, string? Comment, IReadOnlyList<string> BecauseOf, IReadOnlyList<DiscoveryTrackDto> Tracks);

/// <param name="UpdatedAt">Dernière actualisation terminée (null : jamais).</param>
/// <param name="Refreshing">Une actualisation attend ou tourne.</param>
public sealed record DiscoveryPageDto(DateTimeOffset? UpdatedAt, bool Refreshing, IReadOnlyList<DiscoveryArtistDto> Artists);
