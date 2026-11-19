namespace SonNuage.Contracts;

/// <summary>
/// « Rejoindre un Jam » : suivre en direct ce que joue l'hôte d'une session d'écoute partagée d'un service externe
/// (Spotify) et le rejouer ici depuis la bibliothèque ou YouTube. Rien n'est lu du service tant
/// qu'aucun compte n'y est relié. Le son du service lui-même n'est jamais récupéré : seuls le titre et la position le
/// sont, et c'est SonNuage qui joue sa propre source.
/// </summary>
public static class Jam
{
    public const string Route = "api/me/jam";
    public const string StreamRoute = Route + "/stream";

    /// <summary>Événement du flux : l'état courant du Jam (JSON de <see cref="JamStateDto"/>).</summary>
    public const string StateEvent = "state";

    /// <summary>
    /// Services reconnaissables depuis un lien collé ou scanné. Deezer n'a pas d'équivalent au Jam (vérifié sur
    /// l'appli 9.0.26) : un lien Deezer est reconnu pour pouvoir le refuser avec un message clair.
    /// </summary>
    public const string Spotify = "spotify";
    public const string Deezer = "deezer";

    public const int MaxLinkLength = 512;
    public const int MaxTokenLength = 4096;
}

/// <summary>
/// Liaison du compte à un service d'écoute (par compte, facultative). Les jetons ne reviennent jamais vers l'appli :
/// seulement le fait qu'ils sont enregistrés et le nom affiché du compte du service.
/// </summary>
public sealed record MusicServiceAccountDto(string Service, bool Connected, string? DisplayName);

/// <summary>
/// Jetons obtenus par l'appli (OAuth sur l'appareil) et confiés au serveur, qui les chiffre. Le serveur s'en sert seul
/// pour suivre le Jam et rafraîchir l'accès ; il ne les renvoie jamais.
/// </summary>
public sealed record MusicServiceTokenRequest(string Service, string AccessToken, string? RefreshToken, long ExpiresInSeconds, string? DisplayName);

/// <summary>Mode d'écoute choisi en rejoignant un Jam.</summary>
public enum JamAudio
{
    /// <summary>Le son reste sur l'hôte : SonNuage affiche le Jam mais ne joue rien ici.</summary>
    RemoteOnly,
    /// <summary>SonNuage joue aussi ici, en phase avec l'hôte, depuis sa propre source.</summary>
    Guest,
}

/// <summary>Rejoindre un Jam : un lien de partage (ou un code scanné), et le mode d'écoute voulu.</summary>
public sealed record JamJoinRequest(string Link, JamAudio Audio)
{
    public static string? Service(string link) =>
        link.Contains("spotify.com", StringComparison.OrdinalIgnoreCase) || link.Contains("spotify.link", StringComparison.OrdinalIgnoreCase)
            ? Jam.Spotify
            : link.Contains("deezer", StringComparison.OrdinalIgnoreCase) ? Jam.Deezer : null;
}

/// <summary>Un titre tel que le service le décrit (avant toute résolution vers une piste SonNuage).</summary>
public sealed record JamTrackDto(string Title, string Artist, string? Album, double DurationSeconds, string? Isrc);

/// <summary>Où en est la résolution d'un titre du Jam vers une piste jouable ici.</summary>
public enum JamResolve
{
    /// <summary>Pas encore cherché, ou rien à jouer (aucun titre).</summary>
    Pending,
    /// <summary>Trouvé dans la bibliothèque ou importé : <see cref="JamStateDto.Resolved"/> est jouable.</summary>
    Matched,
    /// <summary>Absent de la bibliothèque, import YouTube en cours.</summary>
    Importing,
    /// <summary>Introuvable (pas dans la bibliothèque et import impossible ou échoué).</summary>
    Unavailable,
}

/// <summary>
/// État courant d'un Jam suivi, poussé aux appareils du compte. Vide (<see cref="Active"/> faux) quand aucun Jam n'est
/// suivi.
/// </summary>
/// <param name="Audio">Mode choisi (le son est joué ici seulement en <see cref="JamAudio.Guest"/>).</param>
/// <param name="PositionSeconds">Position de l'hôte au moment de <see cref="AsOfUnixMs"/> (à extrapoler si en lecture).</param>
/// <param name="Resolved">La piste SonNuage à jouer quand <see cref="Resolve"/> vaut <see cref="JamResolve.Matched"/>.</param>
public sealed record JamStateDto(
    bool Active,
    string Service,
    JamAudio Audio,
    string? HostName,
    JamTrackDto? Track,
    bool IsPlaying,
    double PositionSeconds,
    long AsOfUnixMs,
    JamResolve Resolve,
    TrackDto? Resolved,
    IReadOnlyList<TrackDto>? Upcoming = null,
    string? SourceTrackId = null)
{
    public static readonly JamStateDto Inactive = new(false, Jam.Spotify, JamAudio.RemoteOnly, null, null, false, 0, 0, JamResolve.Pending, null);
}
