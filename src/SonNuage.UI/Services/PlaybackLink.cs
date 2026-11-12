using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Commande de lecture envoyée à l'appareil qui joue.</summary>
public sealed record PlayerCommand(
    string Action,
    double Value = 0,
    IReadOnlyList<TrackDto>? Tracks = null,
    int Index = 0,
    bool? Shuffle = null,
    bool Flag = false,
    RepeatMode Repeat = RepeatMode.Off,
    TrackDto? Track = null);

/// <summary>
/// Message entre lecteurs. Types : hello (nouvel arrivant), claim (je joue désormais), state (état de celui qui joue,
/// file omise si inchangée : <see cref="HasQueue"/>), command (à exécuter par celui qui joue), bye (je pars).
/// <see cref="FromName"/> : nom de l'appareil expéditeur, ajouté par le relais du serveur.
/// </summary>
public sealed record PlaybackMessage(
    string Type,
    string From,
    string? To = null,
    PlayerState? State = null,
    bool HasQueue = false,
    PlayerCommand? Command = null,
    string? FromName = null);

/// <summary>
/// Canal entre les lecteurs d'un même compte, sur tous ses appareils : le relais du serveur
/// (<see cref="ServerPlaybackLink"/>).
/// </summary>
public interface IPlaybackLink
{
    string DeviceId { get; }
    Func<PlaybackMessage, Task>? OnMessage { get; set; }
    /// <summary>Canal (r)ouvert : les messages envoyés pendant la coupure sont perdus, chacun se réannonce.</summary>
    Func<Task>? OnConnected { get; set; }
    Task StartAsync();
    Task SendAsync(PlaybackMessage message);
}

/// <summary>Lecteur local pilotable par <see cref="LinkedPlayer"/> : reprend un état publié ailleurs, ou se met en retrait.</summary>
public interface IAdoptablePlayer
{
    Task AdoptAsync(PlayerState state, bool autoplay);
    /// <summary>Un autre lecteur prend la main : on se tait (la file reste).</summary>
    Task ReleaseAsync();
}

/// <summary>Lecteur qui peut jouer ailleurs (autre onglet, autre appareil) et être ramené ici.</summary>
public interface IConnectPlayer
{
    /// <summary>La lecture se fait ailleurs : l'état affiché et les commandes sont ceux de l'autre lecteur.</summary>
    bool IsRemote { get; }
    /// <summary>Identifiant de l'appareil qui joue, quand c'est un autre.</summary>
    string? RemoteId { get; }
    /// <summary>Nom de l'appareil qui joue, quand c'est un autre.</summary>
    string? RemoteName { get; }
    Task PlayHereAsync();
}
