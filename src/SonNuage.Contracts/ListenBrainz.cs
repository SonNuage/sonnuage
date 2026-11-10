namespace SonNuage.Contracts;

/// <summary>
/// Liaison du compte à ListenBrainz (facultative). Le jeton ne revient jamais vers l'appli : seulement le fait
/// qu'il est enregistré et le nom ListenBrainz qu'il désigne.
/// </summary>
/// <param name="Submit">Envoi des écoutes activé (désactivé par défaut) : seules les écoutes faites depuis partent.</param>
/// <param name="Sent">Écoutes déjà envoyées.</param>
public sealed record ListenBrainzAccountDto(bool Connected, string? UserName, bool Submit, int Sent);

public sealed record ListenBrainzTokenRequest(string Token)
{
    public const int MaxLength = 128;
}

public sealed record ListenBrainzSubmitRequest(bool Enabled);

/// <summary>
/// Recommandations personnelles de ListenBrainz : <see cref="Tracks"/> sont dans la bibliothèque, <see cref="Outside"/>
/// n'y sont pas (seulement pour les comptes qui peuvent demander un téléchargement).
/// </summary>
public sealed record ListenBrainzRecommendationsDto(IReadOnlyList<TrackDto> Tracks, IReadOnlyList<ExternalRecordingDto> Outside);

/// <summary>Enregistrement MusicBrainz absent de la bibliothèque.</summary>
public sealed record ExternalRecordingDto(string RecordingMbid, string Title, string Artist, string? Release);
