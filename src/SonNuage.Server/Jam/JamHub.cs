using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SonNuage.Contracts;

namespace SonNuage.Server.Jam;

/// <summary>
/// Chef d'orchestre des Jams suivis : un <see cref="JamFollower"/> par compte au plus, et les flux Server-Sent Events
/// des appareils abonnés. Quand un compte suit un Jam, l'état est poussé à tous ses appareils ; en mémoire, un
/// redémarrage du serveur arrête les suivis (l'appli les relancera).
/// </summary>
public sealed class JamHub(IServiceScopeFactory scopes, ILoggerFactory logs) : IAsyncDisposable
{
    /// <summary>Même forme que ce que lit l'appli (web defaults, énumérations en nombres) : surtout pas le convertisseur chaîne des endpoints.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger _log = logs.CreateLogger<JamHub>();
    private readonly ConcurrentDictionary<Guid, JamFollower> _followers = new();
    private readonly ConcurrentDictionary<Guid, JamStateDto> _states = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<string>>> _subscribers = new();

    /// <summary>Attente maximale de la réponse de Spotify au premier « join », pour dire tout de suite si c'est refusé.</summary>
    private static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// (Re)commence à suivre le Jam désigné par un jeton de partage, dans le mode d'écoute choisi. Rend faux si Spotify
    /// refuse de nous y faire entrer (Jam fini, compte exclu…) : rien n'est alors suivi.
    /// </summary>
    public async Task<bool> JoinAsync(Guid userId, string joinToken, JamAudio audio, CancellationToken ct)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            return await JoinLockedAsync(userId, joinToken, audio, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> JoinLockedAsync(Guid userId, string joinToken, JamAudio audio, CancellationToken ct)
    {
        await StopAsync(userId);
        var follower = new JamFollower(userId, joinToken, audio, scopes, Publish, Ended, _log);
        _followers[userId] = follower;
        bool joined;
        try
        {
            joined = await follower.Joined.WaitAsync(JoinWait, ct);
        }
        catch (TimeoutException)
        {
            // Spotify lent : on laisse le suivi continuer, il finira seul si le « join » échoue.
            return true;
        }
        if (!joined) await StopAsync(userId);
        return joined;
    }

    /// <summary>
    /// Le suivi s'est arrêté de lui-même (Jam fini, exclu, connexion perdue pour de bon) : les appareils l'apprennent.
    /// Seulement s'il est toujours le suivi du compte (un nouveau « join » a pu le remplacer entre-temps).
    /// </summary>
    private void Ended(JamFollower follower)
    {
        if (!_followers.TryRemove(new KeyValuePair<Guid, JamFollower>(follower.UserId, follower))) return;
        _log.LogInformation("Jam : fin du suivi pour le compte {User}", follower.UserId);
        Publish(follower.UserId, JamStateDto.Inactive);
        _states.TryRemove(follower.UserId, out _);
        // Pas d'attente ici : on est dans la boucle du suivi, qui se termine juste après.
        _ = follower.DisposeAsync().AsTask();
    }

    /// <summary>Arrête de suivre : l'état redevient inactif pour tous les appareils du compte.</summary>
    public async Task LeaveAsync(Guid userId)
    {
        var gate = Gate(userId);
        await gate.WaitAsync();
        try
        {
            await StopAsync(userId);
            Publish(userId, JamStateDto.Inactive);
            _states.TryRemove(userId, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Un seul « join » ou « leave » à la fois par compte : deux appareils qui rejoignent ensemble ne doivent
    /// pas laisser un suivi orphelin (toujours connecté à Spotify, jamais arrêté).</summary>
    private SemaphoreSlim Gate(Guid userId) => _gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public JamStateDto Current(Guid userId) =>
        _followers.TryGetValue(userId, out var f) ? f.Current() : JamStateDto.Inactive;

    /// <summary>Abonne un appareil au flux d'état du compte. L'état courant est livré tout de suite.</summary>
    public (ChannelReader<string> Reader, IDisposable Lease) Subscribe(Guid userId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        var devices = _subscribers.GetOrAdd(userId, _ => new());
        devices[id] = channel;
        // État recalculé maintenant (pas le dernier publié) : son AsOfUnixMs sert à l'appareil d'horloge de référence.
        channel.Writer.TryWrite(Serialize(_followers.ContainsKey(userId) ? Current(userId) : _states.GetValueOrDefault(userId, JamStateDto.Inactive)));
        return (channel.Reader, new Lease(() =>
        {
            if (_subscribers.TryGetValue(userId, out var d)) d.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }));
    }

    private void Publish(Guid userId, JamStateDto state)
    {
        _states[userId] = state;
        if (!_subscribers.TryGetValue(userId, out var devices)) return;
        var json = Serialize(state);
        foreach (var channel in devices.Values) channel.Writer.TryWrite(json);
    }

    private async Task StopAsync(Guid userId)
    {
        if (_followers.TryRemove(userId, out var follower)) await follower.DisposeAsync();
    }

    /// <summary>
    /// Sérialise un état comme le lit l'appli (énumérations en nombres, web defaults). À utiliser aussi pour les
    /// réponses one-shot (<c>/join</c>, <c>/state</c>) afin qu'elles correspondent au flux et non au convertisseur
    /// chaîne global des endpoints.
    /// </summary>
    public static string Serialize(JamStateDto state) => JsonSerializer.Serialize(state, Json);

    public async ValueTask DisposeAsync()
    {
        foreach (var follower in _followers.Values) await follower.DisposeAsync();
        _followers.Clear();
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
