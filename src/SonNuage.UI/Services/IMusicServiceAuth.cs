using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Connexion à un service d'écoute (Spotify…) depuis l'appareil : ouvre la page de connexion du service, en revient
/// avec les jetons, à confier ensuite au serveur. L'appli de bureau et Android savent le faire (navigateur système
/// et boucle locale) ; le web ne le peut pas (<see cref="CanConnect"/> faux) et renvoie à l'appli.
/// </summary>
public interface IMusicServiceAuth
{
    /// <summary>Cet appareil sait relier ce service tout seul.</summary>
    bool CanConnect(string service);

    /// <summary>
    /// Déroule la connexion au service sur l'appareil. Renvoie les jetons prêts à confier au serveur, ou null si
    /// l'utilisateur a annulé ou si la connexion a échoué.
    /// </summary>
    Task<MusicServiceTokenRequest?> ConnectAsync(string service, CancellationToken ct);
}

/// <summary>Par défaut (web) : aucune connexion directe possible ; l'utilisateur relie le service depuis l'appli.</summary>
public sealed class NoMusicServiceAuth : IMusicServiceAuth
{
    public bool CanConnect(string service) => false;
    public Task<MusicServiceTokenRequest?> ConnectAsync(string service, CancellationToken ct) => Task.FromResult<MusicServiceTokenRequest?>(null);
}
