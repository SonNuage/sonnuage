using Localify.Contracts;

namespace Localify.UI.Services;

/// <summary>Commande de lecture envoyée à l'appareil qui joue (un autre onglet aujourd'hui, un autre appareil demain).</summary>
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
/// </summary>
public sealed record PlaybackMessage(
    string Type,
    string From,
    string? To = null,
    PlayerState? State = null,
    bool HasQueue = false,
    PlayerCommand? Command = null);

/// <summary>
/// Canal entre les lecteurs d'un même utilisateur. Les onglets d'un navigateur passent par BroadcastChannel ;
/// un transport serveur (SignalR) pourra relier PC et téléphone sans changer <see cref="LinkedPlayer"/>.
/// </summary>
public interface IPlaybackLink
{
    string DeviceId { get; }
    Func<PlaybackMessage, Task>? OnMessage { get; set; }
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
    Task PlayHereAsync();
}
