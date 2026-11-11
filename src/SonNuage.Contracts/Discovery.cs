namespace SonNuage.Contracts;

/// <summary>Parution (groupe de parutions MusicBrainz) d'un artiste de la bibliothèque : sortie récente, à venir, ou absente.</summary>
/// <param name="Type">« album », « ep » ou « single ».</param>
/// <param name="Date">Première sortie, éventuellement partielle (« 2026-11-20 », « 2026 »).</param>
/// <param name="Upcoming">Pas encore sortie.</param>
/// <param name="AlbumId">Album de la bibliothèque qui lui correspond ; null si elle n'y est pas.</param>
public sealed record ArtistReleaseDto(Guid ReleaseGroupId, string Title, Guid ArtistId, string ArtistName, string Type, string? Date, bool Upcoming,
    Guid? AlbumId = null);

/// <summary>Titre phare d'un artiste à découvrir, retrouvé sur YouTube quand c'est possible (sinon <paramref name="VideoId"/> nul).</summary>
/// <param name="PreviewReady">Extrait déjà téléchargé : il se lit tout de suite.</param>
/// <param name="ImportStatus">État de l'import YouTube de cette vidéo, si elle est déjà connue du serveur.</param>
public sealed record DiscoveryTrackDto(Guid Id, string Title, double? DurationSeconds, string? ReleaseName, string? VideoId, string? VideoTitle,
    string? Channel, string? ThumbnailUrl, int? MatchScore, bool PreviewReady, string? ImportStatus);

/// <summary>Artiste proche de ceux de la bibliothèque. <paramref name="BecauseOf"/> : les artistes de la bibliothèque qui y mènent.</summary>
/// <param name="AddedAt">Proposé pour la première fois (les plus récents d'abord ; retiré au bout de 7 jours).</param>
/// <param name="Tracks">Les premiers titres phares (réglage « titres par artiste »).</param>
/// <param name="MoreTracks">Titres phares suivants, à demander par <c>/api/discovery/artists/{id}/tracks</c>.</param>
public sealed record DiscoveryArtistDto(Guid Id, string Name, string? Comment, IReadOnlyList<string> BecauseOf, IReadOnlyList<DiscoveryTrackDto> Tracks,
    DateTimeOffset AddedAt = default, int MoreTracks = 0);

/// <summary>Titres phares suivants d'un artiste à découvrir, et combien il en reste après eux.</summary>
public sealed record DiscoveryMoreDto(IReadOnlyList<DiscoveryTrackDto> Tracks, int Remaining);

/// <param name="UpdatedAt">Dernière actualisation terminée (null : jamais).</param>
/// <param name="Refreshing">Une actualisation attend ou tourne.</param>
public sealed record DiscoveryPageDto(DateTimeOffset? UpdatedAt, bool Refreshing, IReadOnlyList<DiscoveryArtistDto> Artists);

/// <summary>
/// Réglages de la découverte (admin). Toutes les <paramref name="RefreshHours"/> heures, l'actualisation ajoute
/// <paramref name="NewPerRun"/> nouveaux artistes (ou plus pour en afficher au moins <paramref name="MinShown"/>),
/// retire ceux proposés depuis plus de <paramref name="MaxAgeDays"/> jours, et garde
/// <paramref name="TracksPerArtist"/> titres phares par artiste.
/// </summary>
public sealed record DiscoverySettingsDto(int RefreshHours, int NewPerRun, int MinShown, int MaxAgeDays, int TracksPerArtist)
{
    public static readonly DiscoverySettingsDto Defaults = new(6, 6, 24, 7, 3);
    public const int MaxRefreshHours = 168;
    public const int MaxNewPerRun = 50;
    public const int MaxMinShown = 200;
    public const int MaxMaxAgeDays = 90;
    public const int MaxTracksPerArtist = 10;
}
