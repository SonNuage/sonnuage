namespace SonNuage.Contracts;

/// <summary>Une ligne de paroles. Sans <paramref name="StartSeconds"/> : paroles non synchronisées.</summary>
public sealed record LyricLineDto(double? StartSeconds, string Text);

/// <param name="Synced">Chaque ligne a son instant de départ, dans l'ordre.</param>
/// <param name="Source">« local » (fichier .lrc à côté du morceau) ou « lrclib ».</param>
public sealed record LyricsDto(bool Synced, IReadOnlyList<LyricLineDto> Lines, string? Source);
