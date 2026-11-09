using System.Text.Json;
using SonNuage.Contracts;
using Microsoft.JSInterop;

namespace SonNuage.UI.Services;

/// <summary>
/// Lecteur de la webview : file en C#, audio et MediaSession en JS (wwwroot/js/player.js). Client web et appli de bureau.
/// </summary>
public sealed class WebPlayer : IPlayer, IRestorablePlayer, IAdoptablePlayer, IAsyncDisposable
{
    private const string QueueStorageKey = "sonnuage.queue";
    private const string ResumeStorageKey = "sonnuage.resume";
    /// <summary>Une écoute compte dans l'historique au-delà de 30 s (ou à la fin d'une piste plus courte).</summary>
    private const double PlayThresholdSeconds = 30;

    private readonly IJSRuntime js;
    private readonly ApiClient api;
    private readonly PlaybackSettings settings;
    private readonly IKeyValueStore store;
    private readonly AccountStore accounts;
    private readonly PlayQueue _queue = new();
    // Clé de la file du compte qui joue, gardée pour la sauver sous ce compte quand on en change (appli de bureau).
    private string? _queueKey;
    private IJSObjectReference? _module;
    private DotNetObjectReference<WebPlayer>? _self;
    private bool _playing;
    private bool _buffering;
    private double _position;
    private double _volume = 1;
    private string? _error;
    private bool _reported;
    // Un seul appel de lecture infinie à la fois (deux changements de piste rapprochés ne doivent pas doubler l'ajout).
    private bool _extending;
    /// <summary>Radio thématique en cours : la file se complète avec ses morceaux.</summary>
    private string? _station;
    /// <summary>Avance gardée par une radio thématique, même sans lecture infinie.</summary>
    private const int StationAhead = 5;

    public event Action? StateChanged;

    public bool SupportsCrossfade => true;

    public WebPlayer(IJSRuntime js, ApiClient api, PlaybackSettings settings, IKeyValueStore store, AccountStore accounts)
    {
        this.js = js;
        this.api = api;
        this.settings = settings;
        this.store = store;
        this.accounts = accounts;
        accounts.ActiveChanged += () => _ = SwitchAccountAsync();
    }

    private string QueueKey => _queueKey ??= accounts.ScopedKey(QueueStorageKey);

    public PlayerState State => new(
        _queue.Items, _queue.Index, _playing, _buffering, _position,
        _queue.Current?.DurationSeconds ?? 0, _queue.Shuffle, _queue.Repeat, _volume, _error);

    private async Task<IJSObjectReference> ModuleAsync()
    {
        if (_module is not null) return _module;
        _module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/SonNuage.UI/js/player.js");
        _self = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("init", _self);
        return _module;
    }

    /// <summary>Restaure la file de la session précédente, en pause.</summary>
    public async Task RestoreAsync()
    {
        var resume = await store.GetAsync(ResumeStorageKey) == "1";
        if (resume) await store.SetAsync(ResumeStorageKey, null);
        var raw = await store.GetAsync(QueueKey);
        if (raw is null) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedQueue>(raw, JsonDefaults.Options);
            if (saved is null || saved.Ids.Count == 0) return;
            var tracks = await api.LookupTracksAsync(saved.Ids) ?? [];
            if (tracks.Count == 0) return;
            var index = Math.Max(0, tracks.FindIndex(t => t.Id == saved.CurrentId));
            _queue.Load(tracks, index, false);
            _queue.Repeat = saved.Repeat;
            _station = saved.Station;
            _position = saved.Position;
            // Après un rechargement pour mise à jour, la musique reprend (si le navigateur autorise la lecture automatique).
            await LoadCurrentAsync(autoplay: resume, startAt: saved.Position);
        }
        catch (Exception ex) when (ex is JsonException or ApiException or HttpRequestException)
        {
        }
    }

    /// <summary>Reprend ici la file d'un autre onglet, dans son ordre de lecture et à sa position.</summary>
    public async Task AdoptAsync(PlayerState state, bool autoplay)
    {
        if (state.Queue.Count == 0) return;
        await ReportCurrentAsync(completed: false);
        _queue.Load(state.Queue, Math.Max(0, state.CurrentIndex), false);
        _queue.Repeat = state.Repeat;
        _station = null;
        _volume = state.Volume;
        await (await ModuleAsync()).InvokeVoidAsync("setVolume", _volume);
        await LoadCurrentAsync(autoplay, startAt: state.PositionSeconds);
    }

    public async Task ReleaseAsync()
    {
        if (_module is not null && _playing) await _module.InvokeVoidAsync("pause");
    }

    public async Task PlayAsync(IReadOnlyList<TrackDto> tracks, int startIndex = 0, bool? shuffle = null)
    {
        if (tracks.Count == 0) return;
        _station = null;
        await ReportCurrentAsync(completed: false);
        var repeat = _queue.Repeat;
        _queue.Load(tracks, shuffle == true && startIndex < 0 ? new Random().Next(tracks.Count) : Math.Max(0, startIndex), shuffle ?? _queue.Shuffle);
        _queue.Repeat = repeat;
        await LoadCurrentAsync(autoplay: true);
    }

    public async Task PlayStationAsync(string stationId, IReadOnlyList<TrackDto> tracks)
    {
        if (tracks.Count == 0) return;
        await ReportCurrentAsync(completed: false);
        _queue.Load(tracks, 0, shuffle: false);
        _queue.Repeat = RepeatMode.Off;
        _station = stationId;
        await LoadCurrentAsync(autoplay: true);
    }

    public async Task TogglePlayAsync()
    {
        if (_queue.Current is null) return;
        var m = await ModuleAsync();
        if (_playing) await m.InvokeVoidAsync("pause");
        else await m.InvokeVoidAsync("play");
    }

    public Task NextAsync() => AdvanceAsync(auto: false);

    public async Task PreviousAsync()
    {
        // Comme partout : au-delà de 3 s, « précédent » revient au début de la piste.
        if (_position > 3)
        {
            await SeekAsync(0);
            return;
        }
        await ReportCurrentAsync(completed: false);
        _queue.MoveTo(_queue.PeekPrevious());
        await LoadCurrentAsync(autoplay: true);
    }

    public async Task SeekAsync(double seconds)
    {
        _position = seconds;
        await (await ModuleAsync()).InvokeVoidAsync("seek", seconds);
        Changed();
    }

    public async Task JumpToAsync(int queueIndex)
    {
        await ReportCurrentAsync(completed: false);
        _queue.MoveTo(queueIndex);
        await LoadCurrentAsync(autoplay: true);
    }

    public async Task SetShuffleAsync(bool shuffle)
    {
        _queue.SetShuffle(shuffle);
        await PrefetchAsync();
        await SaveAsync();
        Changed();
    }

    public async Task SetRepeatAsync(RepeatMode mode)
    {
        _queue.Repeat = mode;
        await PrefetchAsync();
        await SaveAsync();
        Changed();
    }

    public async Task SetVolumeAsync(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        await (await ModuleAsync()).InvokeVoidAsync("setVolume", _volume);
        Changed();
    }

    public async Task EnqueueAsync(IReadOnlyList<TrackDto> tracks, bool playNext)
    {
        var wasEmpty = _queue.Current is null;
        _queue.Enqueue(tracks, playNext);
        if (wasEmpty) await LoadCurrentAsync(autoplay: true);
        else await PrefetchAsync();
        await SaveAsync();
        Changed();
    }

    public async Task RemoveAtAsync(int queueIndex)
    {
        var removingCurrent = queueIndex == _queue.Index;
        _queue.RemoveAt(queueIndex);
        if (removingCurrent)
        {
            if (_queue.Current is null) await (await ModuleAsync()).InvokeVoidAsync("stop");
            else await LoadCurrentAsync(autoplay: _playing);
        }
        else
        {
            await PrefetchAsync();
        }
        await SaveAsync();
        Changed();
    }

    public async Task ReloadSettingsAsync()
    {
        await settings.LoadAsync();
        if (_module is not null) await _module.InvokeVoidAsync("setCrossfade", settings.CrossfadeSeconds);
        await ExtendAsync();
        await PrefetchAsync();
        Changed();
    }

    public void UpdateTrack(TrackDto track)
    {
        _queue.Replace(track);
        Changed();
    }

    private async Task AdvanceAsync(bool auto)
    {
        await ReportCurrentAsync(completed: auto);
        var next = _queue.PeekNext(auto);
        if (next < 0)
        {
            _playing = false;
            _position = 0;
            await (await ModuleAsync()).InvokeVoidAsync("seek", 0);
            await (await ModuleAsync()).InvokeVoidAsync("pause");
            Changed();
            return;
        }
        _queue.MoveTo(next);
        await LoadCurrentAsync(autoplay: true, crossfade: auto ? settings.CrossfadeSeconds : 0);
    }

    private async Task LoadCurrentAsync(bool autoplay, double startAt = 0, double crossfade = 0)
    {
        var track = _queue.Current;
        if (track is null) return;
        _error = null;
        _reported = false;
        _position = startAt;
        _buffering = autoplay;
        Changed();

        var url = await api.StreamUrlAsync(track.Id, settings.Quality);
        if (url is null)
        {
            _error = Localizer.Current?["player.errors.sessionExpired"] ?? "Session expired";
            Changed();
            return;
        }
        var m = await ModuleAsync();
        await m.InvokeVoidAsync("load", Key(track), url, startAt, autoplay, track.DurationSeconds, crossfade);
        await m.InvokeVoidAsync("setCrossfade", settings.CrossfadeSeconds);
        await m.InvokeVoidAsync("setMetadata", new
        {
            title = track.Title,
            artist = track.ArtistName,
            album = track.AlbumTitle,
            artwork = track.HasCover ? api.CoverUrl(track.AlbumId, 600) : null,
        });
        await ExtendAsync();
        await PrefetchAsync();
        await SaveAsync();
    }

    /// <summary>Lecture infinie : complète la file pour garder l'avance réglée après le morceau en cours.</summary>
    private async Task ExtendAsync()
    {
        if (_queue.Repeat != RepeatMode.Off || _extending) return;
        if (_station is { } station)
        {
            await ExtendStationAsync(station);
            return;
        }
        if (!settings.InfinitePlay) return;
        var needed = InfinitePlay.Needed(_queue.Items.Count, _queue.Index, settings.InfiniteAhead);
        if (needed == 0) return;
        _extending = true;
        try
        {
            var ids = _queue.Items.Select(t => t.Id).ToList();
            var request = new RadioRequest(InfinitePlay.Seeds(ids, _queue.Index), ids.TakeLast(RadioRequest.MaxIds).ToList(), needed);
            if (await api.RadioAsync(request) is { Count: > 0 } tracks) _queue.Enqueue(tracks, playNext: false);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // Sans réseau, la file s'arrêtera simplement à son dernier morceau.
        }
        finally
        {
            _extending = false;
        }
    }

    /// <summary>Suite de la radio thématique ; si elle n'est plus proposée (fin de saison), la lecture infinie reprend la main.</summary>
    private async Task ExtendStationAsync(string station)
    {
        var needed = InfinitePlay.Needed(_queue.Items.Count, _queue.Index, Math.Max(settings.InfiniteAhead, StationAhead));
        if (needed == 0) return;
        _extending = true;
        try
        {
            var exclude = _queue.Items.Select(t => t.Id).TakeLast(500).ToList();
            var tracks = await api.RadioStationNextAsync(station, new StationNextRequest(exclude, needed));
            if (tracks is { Count: > 0 }) _queue.Enqueue(tracks, playNext: false);
        }
        catch (ApiException)
        {
            _station = null;
        }
        catch (HttpRequestException)
        {
            // Sans réseau, la file s'arrêtera simplement à son dernier morceau.
        }
        finally
        {
            _extending = false;
        }
    }

    private async Task PrefetchAsync()
    {
        if (_module is null) return;
        var items = new List<object>();
        foreach (var t in _queue.Upcoming(settings.PrefetchCount))
        {
            if (await api.StreamUrlAsync(t.Id, settings.Quality) is { } url) items.Add(new { key = Key(t), url });
        }
        await _module.InvokeVoidAsync("prefetch", items);
    }

    private string Key(TrackDto t) => $"{t.Id:N}-{settings.Quality.ToWire()}";

    private async Task ReportCurrentAsync(bool completed)
    {
        var track = _queue.Current;
        if (track is null || _reported) return;
        if (!completed && _position < Math.Min(PlayThresholdSeconds, track.DurationSeconds * 0.5)) return;
        _reported = true;
        try
        {
            await api.ReportPlayAsync(new PlayEventRequest(track.Id, completed ? track.DurationSeconds : _position, completed));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
        }
    }

    public async Task PrepareReloadAsync()
    {
        await SaveAsync();
        await store.SetAsync(ResumeStorageKey, _playing ? "1" : null);
    }

    private async Task SaveAsync()
    {
        var saved = new SavedQueue(_queue.Items.Take(500).Select(t => t.Id).ToList(), _queue.Current?.Id, _position, _queue.Repeat, _station);
        await store.SetAsync(QueueKey, JsonSerializer.Serialize(saved, JsonDefaults.Options));
    }

    /// <summary>
    /// Plusieurs comptes (appli de bureau) : la file du compte quitté est sauvée sous sa clé, la lecture s'arrête, puis
    /// la file du nouveau compte est chargée en pause.
    /// </summary>
    private async Task SwitchAccountAsync()
    {
        // Pas de file chargée avant (premier compte, juste connecté) : la mise en page la restaure elle-même.
        var previous = _queueKey;
        if (_queue.Current is not null) await SaveAsync();
        _queueKey = null;
        _queue.Load([], 0, false);
        _station = null;
        _playing = false;
        _position = 0;
        if (_module is not null) await _module.InvokeVoidAsync("stop");
        Changed();
        if (previous is not null && accounts.Active is not null) await RestoreAsync();
    }

    private void Changed() => StateChanged?.Invoke();

    // ---- Rappels JS ----

    [JSInvokable]
    public Task OnPlaying()
    {
        _playing = true;
        _buffering = false;
        _error = null;
        Changed();
        return (_module?.InvokeVoidAsync("setPlaybackState", true) ?? ValueTask.CompletedTask).AsTask();
    }

    [JSInvokable]
    public async Task OnPaused()
    {
        _playing = false;
        _buffering = false;
        Changed();
        if (_module is not null) await _module.InvokeVoidAsync("setPlaybackState", false);
        await SaveAsync();
    }

    [JSInvokable]
    public void OnBuffering()
    {
        _buffering = true;
        Changed();
    }

    [JSInvokable]
    public void OnTime(double seconds)
    {
        _position = seconds;
        Changed();
    }

    [JSInvokable]
    public Task OnEnded() => AdvanceAsync(auto: true);

    /// <summary>Fin proche, fondu enchaîné réglé : le morceau suivant démarre pendant que celui-ci s'éteint.</summary>
    [JSInvokable]
    public Task OnNearEnd() => _queue.PeekNext(auto: true) >= 0 ? AdvanceAsync(auto: true) : Task.CompletedTask;

    [JSInvokable]
    public void OnError(string key)
    {
        _error = Localizer.Current?[key] ?? key;
        _playing = false;
        _buffering = false;
        Changed();
    }

    [JSInvokable]
    public Task OnRemote(string action, double value) => action switch
    {
        "play" => _playing ? Task.CompletedTask : TogglePlayAsync(),
        "pause" => _playing ? TogglePlayAsync() : Task.CompletedTask,
        "next" => NextAsync(),
        "previous" => PreviousAsync(),
        "seek" => SeekAsync(value),
        _ => Task.CompletedTask,
    };

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
        _self?.Dispose();
    }

    /// <param name="Station">Radio thématique en cours : elle continue après un rechargement de la page.</param>
    private sealed record SavedQueue(List<Guid> Ids, Guid? CurrentId, double Position, RepeatMode Repeat, string? Station = null);
}
