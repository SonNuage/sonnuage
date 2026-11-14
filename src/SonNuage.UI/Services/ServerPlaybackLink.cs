using System.Net;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Relie les lecteurs de tous les appareils du compte par le serveur (<see cref="DeviceLink"/>) : un flux
/// Server-Sent Events pour recevoir, un POST par message envoyé. Le flux se rouvre seul après une coupure (réseau,
/// redémarrage du serveur), suit le compte actif et la connexion, et se rouvre sous le nouveau nom de l'appareil.
/// </summary>
public sealed class ServerPlaybackLink : IPlaybackLink, IDisposable
{
    /// <summary>Attente avant chaque nouvelle tentative, puis la dernière en boucle.</summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Au démarrage, le temps laissé au flux pour s'ouvrir avant que le lecteur ne s'annonce.</summary>
    private static readonly TimeSpan FirstConnection = TimeSpan.FromSeconds(3);

    /// <summary>Un message attend au plus ce temps que le flux (r)ouvre, au lieu d'être perdu.</summary>
    private static readonly TimeSpan SendWait = TimeSpan.FromSeconds(5);

    private readonly ApiClient _api;
    private readonly AuthSession _auth;
    private readonly AccountStore _accounts;
    private readonly DeviceIdentity _identity;
    private readonly object _lock = new();
    private CancellationTokenSource? _run;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private bool _connected;

    public ServerPlaybackLink(ApiClient api, AuthSession auth, AccountStore accounts, DeviceIdentity identity)
    {
        _api = api;
        _auth = auth;
        _accounts = accounts;
        _identity = identity;
    }

    /// <summary>Un identifiant par lancement (deux onglets d'un navigateur sont deux lecteurs), même nom.</summary>
    public string DeviceId { get; } = Guid.NewGuid().ToString("N")[..12];

    public Func<PlaybackMessage, Task>? OnMessage { get; set; }

    public Func<Task>? OnConnected { get; set; }

    /// <summary>Le flux vient de s'ouvrir : cet appareil figure parmi ceux du compte (liste des appareils à relire).</summary>
    public event Action? Connected;

    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (!_started)
            {
                _started = true;
                _auth.Changed += OnAuthChanged;
                _accounts.ActiveChanged += Restart;
                _identity.Changed += Restart;
                Restart();
            }
        }
        await Task.WhenAny(_ready.Task, Task.Delay(FirstConnection));
    }

    /// <summary>
    /// Envoie au relais. Flux en cours de réouverture : le message l'attend. Refusé parce que le serveur ne connaît
    /// plus cet appareil (flux perdu sans qu'on le sache, écran de montre éteint) : le flux est rouvert et le message
    /// renvoyé une fois.
    /// </summary>
    public async Task SendAsync(PlaybackMessage message)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var connection = await ConnectionAsync();
            if (connection is null) return;
            try
            {
                await _api.SendDeviceMessageAsync(message);
                return;
            }
            catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict)
            {
                Restart(ifStill: connection);
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
            {
                // Relais coupé : le flux se rouvrira, et chacun se réannoncera (OnConnected).
                return;
            }
        }
    }

    /// <summary>Rouvre le flux tout de suite (appli revenue au premier plan, réseau peut-être changé entre-temps).</summary>
    public void Reconnect() => Restart();

    /// <summary>Le flux ouvert (attendu au plus <see cref="SendWait"/>), ou null s'il ne s'ouvre pas.</summary>
    private async Task<TaskCompletionSource?> ConnectionAsync()
    {
        TaskCompletionSource ready;
        lock (_lock) ready = _ready;
        await Task.WhenAny(ready.Task, Task.Delay(SendWait));
        lock (_lock) return _connected ? _ready : null;
    }

    private void OnAuthChanged()
    {
        // Connexion ou déconnexion : le flux suit. Un simple rafraîchissement de jeton ne le coupe pas.
        lock (_lock)
        {
            if (_auth.IsAuthenticated == (_run is not null)) return;
        }
        Restart();
    }

    private void Restart() => Restart(ifStill: null);

    /// <summary>Rouvre le flux, sauf si <paramref name="ifStill"/> n'est plus la connexion en cours (déjà rouverte).</summary>
    private void Restart(TaskCompletionSource? ifStill)
    {
        CancellationTokenSource run;
        lock (_lock)
        {
            if (ifStill is not null && !ReferenceEquals(ifStill, _ready)) return;
            _run?.Cancel();
            _run = null;
            MarkDisconnected();
            if (!_auth.IsAuthenticated) return;
            _run = run = new CancellationTokenSource();
        }
        _ = RunAsync(run.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await _identity.LoadAsync();
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in _api.DeviceLinkAsync(DeviceId, _identity.Name, _identity.Platform, ct))
                {
                    if (e.Event == DeviceLink.ReadyEvent)
                    {
                        failures = 0;
                        lock (_lock)
                        {
                            _connected = true;
                            _ready.TrySetResult();
                        }
                        if (OnConnected is { } connected) await connected();
                        Connected?.Invoke();
                    }
                    else if (e.Event == DeviceLink.MessageEvent && Parse(e.Data) is { } message && OnMessage is { } handle)
                    {
                        await handle(message);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                // Session terminée : le flux reprendra à la prochaine connexion (AuthSession.Changed).
                lock (_lock) MarkDisconnected();
                return;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or IOException or TimeoutException or OperationCanceledException or JsonException)
            {
                // Coupure : nouvelle tentative plus bas.
            }
            lock (_lock)
            {
                // Un flux remplacé se ferme parfois après que son successeur s'est ouvert : il ne doit pas le défaire.
                if (ct.IsCancellationRequested) return;
                MarkDisconnected();
            }
            try
            {
                await Task.Delay(Backoff[Math.Min(failures++, Backoff.Length - 1)], ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Hors connexion : les messages à venir attendent la prochaine (sous verrou).</summary>
    private void MarkDisconnected()
    {
        _connected = false;
        if (_ready.Task.IsCompleted) _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private PlaybackMessage? Parse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<PlaybackMessage>(json, JsonDefaults.Options);
            if (message is null || message.From == DeviceId || message.To is { } to && to != DeviceId) return null;
            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _auth.Changed -= OnAuthChanged;
        _accounts.ActiveChanged -= Restart;
        _identity.Changed -= Restart;
        _run?.Cancel();
    }
}
