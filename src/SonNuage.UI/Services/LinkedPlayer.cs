using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Un seul lecteur actif à la fois parmi les appareils du compte reliés par <see cref="IPlaybackLink"/> : celui qui se met à jouer
/// le signale (claim) et les autres se taisent, puis affichent son état et lui transmettent leurs commandes,
/// comme une télécommande. Quand il disparaît, chacun reprend sa file, en pause. « Lire ici » ramène la lecture.
/// </summary>
public sealed class LinkedPlayer : IPlayer, IRestorablePlayer, IConnectPlayer
{
    /// <summary>Le temps pour un lecteur actif de répondre à un nouvel arrivant.</summary>
    private static readonly TimeSpan HelloWait = TimeSpan.FromMilliseconds(300);

    private readonly IPlayer _local;
    private readonly IAdoptablePlayer _adoptable;
    private readonly IPlaybackLink _link;
    private Task? _started;
    // Lecteur qui joue : null (aucun), le nôtre, ou un autre.
    private string? _active;
    private string? _activeName;
    private PlayerState _remote = PlayerState.Empty;
    private IReadOnlyList<TrackDto>? _sentQueue;
    private int _sentCount = -1;

    public LinkedPlayer(IPlayer local, IAdoptablePlayer adoptable, IPlaybackLink link)
    {
        _local = local;
        _adoptable = adoptable;
        _link = link;
        _local.StateChanged += OnLocalChanged;
        _link.OnMessage = OnMessageAsync;
        _link.OnConnected = OnConnectedAsync;
    }

    public event Action? StateChanged;

    private string Me => _link.DeviceId;

    public bool IsRemote => _active is not null && _active != Me;

    public string? RemoteId => IsRemote ? _active : null;

    public string? RemoteName => IsRemote ? _activeName : null;

    public PlayerState State => IsRemote ? _remote : _local.State;

    // Le fondu est un réglage du compte, appliqué par l'onglet qui joue : on l'affiche dès que ce lecteur sait le faire.
    public bool SupportsCrossfade => _local.SupportsCrossfade;

    public async Task RestoreAsync()
    {
        await EnsureStartedAsync();
        // Un autre onglet joue déjà : on le pilote au lieu de reprendre la file une seconde fois.
        if (!IsRemote && _local is IRestorablePlayer restorable) await restorable.RestoreAsync();
    }

    public Task PrepareReloadAsync() =>
        !IsRemote && _local is IRestorablePlayer restorable ? restorable.PrepareReloadAsync() : Task.CompletedTask;

    private Task EnsureStartedAsync() => _started ??= StartAsync();

    private async Task StartAsync()
    {
        await _link.StartAsync();
        await _link.SendAsync(new PlaybackMessage("hello", Me));
        await Task.Delay(HelloWait);
    }

    /// <summary>Après une coupure du relais : celui qui joue republie tout, les autres redemandent l'état.</summary>
    private async Task OnConnectedAsync()
    {
        if (_active == Me)
        {
            _sentQueue = null;
            await _link.SendAsync(new PlaybackMessage("claim", Me));
            await PublishAsync(_local.State);
        }
        else
        {
            await _link.SendAsync(new PlaybackMessage("hello", Me));
        }
    }

    public async Task PlayHereAsync()
    {
        if (!IsRemote) return;
        var state = _remote;
        _active = Me;
        _sentQueue = null;
        await _link.SendAsync(new PlaybackMessage("claim", Me));
        await _adoptable.AdoptAsync(state, autoplay: true);
        StateChanged?.Invoke();
    }

    // ---- IPlayer : ici, ou transmis au lecteur actif ----

    public Task PlayAsync(IReadOnlyList<TrackDto> tracks, int startIndex = 0, bool? shuffle = null) =>
        IsRemote ? SendAsync(new PlayerCommand("play", Tracks: tracks, Index: startIndex, Shuffle: shuffle)) : _local.PlayAsync(tracks, startIndex, shuffle);

    /// <summary>Sur un autre appareil, la radio part comme une simple liste : seul ce lecteur-ci sait demander la suite.</summary>
    public Task PlayStationAsync(string stationId, IReadOnlyList<TrackDto> tracks) =>
        IsRemote ? PlayAsync(tracks, 0, shuffle: false) : _local.PlayStationAsync(stationId, tracks);

    public Task TogglePlayAsync() => IsRemote ? SendAsync(new PlayerCommand("toggle")) : _local.TogglePlayAsync();

    public Task NextAsync() => IsRemote ? SendAsync(new PlayerCommand("next")) : _local.NextAsync();

    public Task PreviousAsync() => IsRemote ? SendAsync(new PlayerCommand("previous")) : _local.PreviousAsync();

    public Task SeekAsync(double seconds)
    {
        if (!IsRemote) return _local.SeekAsync(seconds);
        _remote = _remote with { PositionSeconds = seconds };
        StateChanged?.Invoke();
        return SendAsync(new PlayerCommand("seek", seconds));
    }

    public Task JumpToAsync(int queueIndex) => IsRemote ? SendAsync(new PlayerCommand("jump", Index: queueIndex)) : _local.JumpToAsync(queueIndex);

    public Task SetShuffleAsync(bool shuffle) => IsRemote ? SendAsync(new PlayerCommand("shuffle", Flag: shuffle)) : _local.SetShuffleAsync(shuffle);

    public Task SetRepeatAsync(RepeatMode mode) => IsRemote ? SendAsync(new PlayerCommand("repeat", Repeat: mode)) : _local.SetRepeatAsync(mode);

    public Task SetVolumeAsync(double volume)
    {
        if (!IsRemote) return _local.SetVolumeAsync(volume);
        _remote = _remote with { Volume = volume };
        StateChanged?.Invoke();
        return SendAsync(new PlayerCommand("volume", volume));
    }

    public Task EnqueueAsync(IReadOnlyList<TrackDto> tracks, bool playNext) =>
        IsRemote ? SendAsync(new PlayerCommand("enqueue", Tracks: tracks, Flag: playNext)) : _local.EnqueueAsync(tracks, playNext);

    public Task RemoveAtAsync(int queueIndex) => IsRemote ? SendAsync(new PlayerCommand("remove", Index: queueIndex)) : _local.RemoveAtAsync(queueIndex);

    public Task ReloadSettingsAsync() => _local.ReloadSettingsAsync();

    public void UpdateTrack(TrackDto track)
    {
        _local.UpdateTrack(track);
        if (!IsRemote) return;
        _remote = _remote with { Queue = _remote.Queue.Select(t => t.Id == track.Id ? track : t).ToList() };
        StateChanged?.Invoke();
        _ = SendAsync(new PlayerCommand("update", Track: track));
    }

    private async Task SendAsync(PlayerCommand command)
    {
        await EnsureStartedAsync();
        await _link.SendAsync(new PlaybackMessage("command", Me, To: _active, Command: command));
    }

    private Task ExecuteAsync(PlayerCommand c) => c.Action switch
    {
        "play" when c.Tracks is { } tracks => _local.PlayAsync(tracks, c.Index, c.Shuffle),
        "toggle" => _local.TogglePlayAsync(),
        "next" => _local.NextAsync(),
        "previous" => _local.PreviousAsync(),
        "seek" => _local.SeekAsync(c.Value),
        "jump" => _local.JumpToAsync(c.Index),
        "shuffle" => _local.SetShuffleAsync(c.Flag),
        "repeat" => _local.SetRepeatAsync(c.Repeat),
        "volume" => _local.SetVolumeAsync(c.Value),
        "enqueue" when c.Tracks is { } tracks => _local.EnqueueAsync(tracks, c.Flag),
        "remove" => _local.RemoveAtAsync(c.Index),
        "update" when c.Track is { } track => Run(() => _local.UpdateTrack(track)),
        _ => Task.CompletedTask,
    };

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    // ---- Synchronisation ----

    private void OnLocalChanged()
    {
        var state = _local.State;
        if (state.IsPlaying && _active != Me)
        {
            // On se met à jouer (bouton, touche média…) : les autres se taisent.
            _active = Me;
            _sentQueue = null;
            _ = Fire(_link.SendAsync(new PlaybackMessage("claim", Me)));
        }
        if (_active == Me) _ = Fire(PublishAsync(state));
        if (!IsRemote) StateChanged?.Invoke();
    }

    private Task PublishAsync(PlayerState state)
    {
        // La file (jusqu'à des centaines de pistes) ne part que quand elle change. La position, à chaque seconde.
        var full = !ReferenceEquals(state.Queue, _sentQueue) || state.Queue.Count != _sentCount;
        if (full)
        {
            _sentQueue = state.Queue;
            _sentCount = state.Queue.Count;
        }
        return _link.SendAsync(new PlaybackMessage("state", Me, State: full ? state : state with { Queue = [] }, HasQueue: full));
    }

    private async Task OnMessageAsync(PlaybackMessage m)
    {
        switch (m.Type)
        {
            case "hello" when _active == Me:
                _sentQueue = null;
                await PublishAsync(_local.State);
                break;
            case "claim":
                await YieldToAsync(m.From, m.FromName);
                break;
            case "state" when m.State is { } state:
                if (_active != m.From) await YieldToAsync(m.From, m.FromName);
                _remote = m.HasQueue ? state : state with { Queue = _remote.Queue };
                StateChanged?.Invoke();
                break;
            case "command" when m.Command is { } command && _active == Me:
                await ExecuteAsync(command);
                break;
            case "bye" when m.From == _active:
                // Le lecteur actif a fermé : on reprend sa file ici, en pause.
                _active = null;
                if (_remote.Queue.Count > 0) await _adoptable.AdoptAsync(_remote with { IsPlaying = false }, autoplay: false);
                StateChanged?.Invoke();
                break;
        }
    }

    private async Task YieldToAsync(string device, string? name)
    {
        var wasLocal = !IsRemote;
        _active = device;
        _activeName = name;
        if (wasLocal) await _adoptable.ReleaseAsync();
        StateChanged?.Invoke();
    }

    private static async Task Fire(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or Microsoft.JSInterop.JSException)
        {
            // Canal indisponible (onglet en cours de fermeture) : la synchronisation reprendra au prochain état.
        }
    }
}
