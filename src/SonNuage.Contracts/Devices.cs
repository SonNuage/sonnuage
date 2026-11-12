namespace SonNuage.Contracts;

/// <summary>
/// Appareils d'un compte : ses lecteurs SonNuage reliés par le serveur (web, Android, bureau, iOS), qui se passent la
/// lecture (<c>GET /api/me/devices/link</c> en Server-Sent Events, <c>POST</c> pour envoyer), et ses applis Subsonic,
/// visibles mais pas pilotables.
/// </summary>
public static class DeviceLink
{
    public const string Route = "api/me/devices";
    public const string LinkRoute = Route + "/link";

    /// <summary>Événement du flux : le relais est ouvert pour cet appareil (envoyé à chaque connexion).</summary>
    public const string ReadyEvent = "ready";

    /// <summary>Événement du flux : un message d'un autre appareil du compte (JSON du message, avec <c>fromName</c>).</summary>
    public const string MessageEvent = "link";

    /// <summary>Types de message relayés : nouvel arrivant, « je joue », état, commande, départ.</summary>
    public static readonly IReadOnlySet<string> MessageTypes = new HashSet<string> { "hello", "claim", "state", "command", "bye" };

    public const int MaxIdLength = 40;
    public const int MaxNameLength = 60;
    public const int MaxPlatformLength = 20;

    /// <summary>Un message (file comprise, jusqu'à quelques milliers de pistes) : au plus 2 Mo.</summary>
    public const int MaxMessageBytes = 2 * 1024 * 1024;
}

/// <summary>Appareil SonNuage connecté au relais du compte, sous le nom que lui a donné son utilisateur.</summary>
public sealed record DeviceDto(string Id, string Name, string Platform);

/// <summary>Appli Subsonic (Symfonium, Feishin…) en train de jouer pour ce compte.</summary>
public sealed record SubsonicAppDto(string Client, TrackDto Track, DateTimeOffset StartedAt);

/// <summary>File enregistrée par une appli Subsonic (<c>savePlayQueue</c>), à reprendre dans SonNuage.</summary>
public sealed record SavedQueueDto(string? ChangedBy, DateTimeOffset ChangedAt, IReadOnlyList<TrackDto> Tracks, int CurrentIndex, double PositionSeconds);

public sealed record DevicesDto(IReadOnlyList<DeviceDto> Devices, IReadOnlyList<SubsonicAppDto> Apps, SavedQueueDto? SavedQueue);
