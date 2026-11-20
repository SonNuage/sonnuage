using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SonNuage.Contracts;

namespace SonNuage.Server.Api;

/// <summary>
/// Relais de lecture entre les appareils d'un même compte (voir <see cref="DeviceLink"/>) : chaque appareil connecté
/// a sa boîte de messages, que son flux vide. Le relais ne lit des messages que l'expéditeur, le destinataire et le
/// type, et y ajoute le nom de l'expéditeur ; le reste (état du lecteur, commandes) passe tel quel. En mémoire : un
/// redémarrage du serveur coupe les flux, que les appareils rouvrent. Les arrivées, départs, annonces (hello,
/// claim) et messages refusés sont journalisés : de quoi comprendre pourquoi un appareil ne voit pas ce que joue un autre.
/// </summary>
public sealed class PlaybackRelay(ILogger<PlaybackRelay> logger)
{
    /// <summary>Un appareil lent ne retient pas les autres : au-delà, ses plus vieux messages sont perdus.</summary>
    private const int Backlog = 256;

    /// <summary>Plus d'appareils que ça sur un compte : sans doute une boucle de reconnexion, on refuse.</summary>
    public const int MaxDevicesPerAccount = 32;

    public sealed class Connection(DeviceDto device)
    {
        public DeviceDto Device { get; } = device;
        public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Connection>> _accounts = new();

    /// <summary>
    /// Ouvre la boîte d'un appareil. Le même identifiant reconnecté remplace l'ancienne connexion, qui se ferme sans
    /// annoncer de départ. Null si le compte a déjà trop d'appareils.
    /// </summary>
    public Connection? Join(Guid userId, DeviceDto device)
    {
        var devices = _accounts.GetOrAdd(userId, _ => new());
        if (!devices.ContainsKey(device.Id) && devices.Count >= MaxDevicesPerAccount)
        {
            logger.LogWarning("Relais : {Device} ({Name}) refusé, {Count} appareils déjà connectés pour {User}", device.Id, device.Name, devices.Count, userId);
            return null;
        }
        var connection = new Connection(device);
        devices.AddOrUpdate(device.Id, connection, (_, old) =>
        {
            old.Outbox.Writer.TryComplete();
            return connection;
        });
        logger.LogInformation("Relais : {Device} ({Name}, {Platform}) connecté pour {User}, {Count} appareil(s)",
            device.Id, device.Name, device.Platform, userId, devices.Count);
        return connection;
    }

    /// <summary>L'appareil part : les autres reçoivent son « bye », sauf s'il s'est déjà reconnecté ailleurs.</summary>
    public void Leave(Guid userId, Connection connection)
    {
        if (!_accounts.TryGetValue(userId, out var devices)) return;
        if (!devices.TryRemove(KeyValuePair.Create(connection.Device.Id, connection))) return;
        connection.Outbox.Writer.TryComplete();
        logger.LogInformation("Relais : {Device} ({Name}) parti pour {User}, {Count} appareil(s)", connection.Device.Id, connection.Device.Name, userId, devices.Count);
        var bye = new JsonObject { ["type"] = "bye", ["from"] = connection.Device.Id, ["fromName"] = connection.Device.Name };
        Deliver(devices, bye, from: connection.Device.Id, to: null);
    }

    public IReadOnlyList<DeviceDto> Devices(Guid userId) =>
        _accounts.TryGetValue(userId, out var devices) ? devices.Values.Select(c => c.Device).OrderBy(d => d.Name).ToList() : [];

    /// <summary>Faux si l'expéditeur n'est pas connecté pour ce compte (message ignoré).</summary>
    public bool Relay(Guid userId, string from, string? to, JsonObject message)
    {
        if (!_accounts.TryGetValue(userId, out var devices) || !devices.TryGetValue(from, out var sender))
        {
            logger.LogInformation("Relais : message {Type} de {Device}, inconnu pour {User}, refusé", message["type"]?.ToString(), from, userId);
            return false;
        }
        message["fromName"] = sender.Device.Name;
        if (message["type"]?.ToString() is "hello" or "claim")
            logger.LogInformation("Relais : {Type} de {Device} ({Name}) vers {Count} autre(s) appareil(s)", message["type"]!.ToString(), from, sender.Device.Name, devices.Count - 1);
        Deliver(devices, message, from, to);
        return true;
    }

    private static void Deliver(ConcurrentDictionary<string, Connection> devices, JsonObject message, string from, string? to)
    {
        var json = message.ToJsonString();
        foreach (var (id, connection) in devices)
        {
            if (id == from || to is not null && id != to) continue;
            connection.Outbox.Writer.TryWrite(json);
        }
    }
}
