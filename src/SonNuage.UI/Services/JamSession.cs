using System.Net;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Côté appli : suit l'état d'un Jam (flux du serveur) et, en mode invité, garde le lecteur local en phase avec l'hôte
/// — même titre (bibliothèque ou import YouTube), même position, mêmes pauses. En mode « hôte seulement », l'état est
/// seulement affiché. Le serveur fait le gros du travail (voir <c>JamHub</c>) ; ici on écoute et on recale.
/// </summary>
public sealed class JamSession : IDisposable
{
    /// <summary>
    /// Écart toléré avant de recaler le lecteur sur l'hôte. Large exprès : chacun écoute de son côté, une seconde ou
    /// deux d'écart ne s'entend pas, alors qu'un saut dans la musique, si.
    /// </summary>
    private const double DriftSeconds = 2.5;
    /// <summary>Cadence de la boucle de synchro (comparaison position locale / position de l'hôte).</summary>
    private static readonly TimeSpan SyncTick = TimeSpan.FromMilliseconds(250);
    /// <summary>
    /// Rattrapage discret : en deçà de <see cref="DriftSeconds"/>, on ne saute pas, on joue un peu plus vite ou plus
    /// lentement (hauteur conservée) jusqu'à rejoindre l'hôte. Gain : vitesse = 1 − écart × gain, bornée à ±MaxNudge.
    /// </summary>
    private const double NudgeStartSeconds = 0.12;
    private const double NudgeStopSeconds = 0.04;
    private const double NudgeGain = 0.04;
    private const double MaxNudge = 0.05;

    /// <summary>Après un recalage, on laisse le lecteur se stabiliser avant d'en mesurer l'effet.</summary>
    private static readonly TimeSpan SeekSettle = TimeSpan.FromMilliseconds(1200);

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly ApiClient _api;
    private readonly IPlayer _player;
    private readonly AuthSession _auth;
    private CancellationTokenSource? _follow;
    /// <summary>Titre de l'hôte (id Spotify) qu'on joue ici, et ce qu'on joue pour lui (bibliothèque ou YouTube).</summary>
    private string? _playingSource;
    private TrackDto? _playing;
    private readonly object _lookupGate = new();
    private string? _lookupSource;
    private Task<YoutubeAudio?>? _lookup;
    /// <summary>Réveille la boucle de synchro dès qu'un état arrive (au lieu d'attendre le prochain tour).</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    // ---- Horloges ----
    /// <summary>Horloge de l'appareil moins celle du serveur (temps de vol compris), estimée comme NTP : minimum de
    /// (réception − AsOfUnixMs) sur les états reçus.</summary>
    private long? _serverClockOffsetMs;

    // ---- Position du lecteur local, horodatée (le lecteur ne la relit que toutes les ~500 ms) ----
    private double _playerPos;
    private long _playerPosAtMs;
    private bool _playerRunning;

    // ---- Recalage ----
    /// <summary>Avance appliquée aux recalages : le temps que met le lecteur à reprendre (appris). Un titre neuf (à
    /// charger) et un saut dans un titre déjà en mémoire n'ont pas le même délai, d'où deux valeurs.</summary>
    private double _coldLeadSeconds = 1.0;
    private double _warmLeadSeconds = 0.2;
    private bool _lastSeekCold;
    private long _lastSeekAtMs;
    private double _speed = 1;
    private double _lastSeekTarget = double.NaN;

    private readonly YoutubeDirect _youtube;

    public JamSession(ApiClient api, IPlayer player, AuthSession auth, YoutubeDirect youtube)
    {
        _youtube = youtube;
        _api = api;
        _player = player;
        _auth = auth;
        _player.StateChanged += OnPlayerState;
    }

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void OnPlayerState()
    {
        var st = _player.State;
        _playerPos = st.PositionSeconds;
        _playerPosAtMs = NowMs;
        _playerRunning = st.IsPlaying && !st.IsBuffering;
    }

    /// <summary>Position estimée du lecteur local maintenant.</summary>
    private double PlayerPositionNow() =>
        _playerPos + (_playerRunning ? Math.Max(0, NowMs - _playerPosAtMs) / 1000.0 * _speed : 0);

    private async Task SetSpeedAsync(double speed)
    {
        speed = Math.Round(speed, 3);
        if (Math.Abs(speed - _speed) < 0.004) return;
        _speed = speed;
        await _player.SetSpeedAsync(speed);
    }

    public JamStateDto State { get; private set; } = JamStateDto.Inactive;
    public event Action? StateChanged;

    /// <summary>Le Jam s'est arrêté sans qu'on le quitte (fini par l'hôte, exclu, connexion perdue pour de bon).</summary>
    public event Action? Ended;

    public Task<MusicServiceAccountDto?> SpotifyAccountAsync() => _api.SpotifyAccountAsync();

    /// <summary>Rejoint un Jam depuis un lien (ou code). Démarre le suivi en direct.</summary>
    public async Task JoinAsync(string link, JamAudio audio)
    {
        var state = await _api.JamJoinAsync(new JamJoinRequest(link, audio));
        _playingSource = null;
        _playing = null;
        Apply(state ?? JamStateDto.Inactive);
        StartFollow();
    }

    /// <summary>Quitte le Jam suivi (le lecteur garde ce qu'il joue).</summary>
    public async Task LeaveAsync()
    {
        StopFollow();
        try
        {
            await _api.JamLeaveAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // Même si l'appel échoue, on cesse de suivre côté appli.
        }
        Apply(JamStateDto.Inactive);
    }

    /// <summary>Reprend le suivi au lancement de l'appli si un Jam est déjà en cours côté serveur.</summary>
    public async Task ResumeAsync()
    {
        if (!_auth.IsAuthenticated)
        {
            // Déconnecté : le Jam reste suivi côté serveur pour ce compte, mais cet appareil ne le pilote plus.
            if (_follow is not null) { StopFollow(); Apply(JamStateDto.Inactive); }
            return;
        }
        if (_follow is not null) return;
        try
        {
            var state = await _api.JamStateAsync();
            if (state is { Active: true })
            {
                Apply(state);
                StartFollow();
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
        }
    }

    private void StartFollow()
    {
        StopFollow();
        var cts = new CancellationTokenSource();
        _follow = cts;
        _serverClockOffsetMs = null;
        _playingSource = null;
        _playing = null;
        _ = _youtube.WarmUpAsync();
        _ = FollowAsync(cts.Token);
        _ = SyncLoopAsync(cts.Token);
    }

    private void StopFollow()
    {
        if (Math.Abs(_speed - 1) > 0.001) { _speed = 1; _ = _player.SetSpeedAsync(1); }
        _follow?.Cancel();
        _follow = null;
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in _api.JamStreamAsync(ct))
                {
                    if (e.Event != Jam.StateEvent) continue;
                    if (Parse(e.Data) is { } state)
                    {
                        failures = 0;
                        Apply(state);
                        if (!state.Active)
                        {
                            StopFollow();
                            Ended?.Invoke();
                            return;
                        }
                    }
                }
            }
            catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                // Session expirée ou déconnexion : on cesse vraiment de suivre (ResumeAsync reprendra après reconnexion).
                StopFollow();
                Apply(JamStateDto.Inactive);
                return;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) return;
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

    private void Apply(JamStateDto state)
    {
        if (state.AsOfUnixMs > 0)
        {
            var sample = NowMs - state.AsOfUnixMs;
            if (_serverClockOffsetMs is not { } best || sample < best) _serverClockOffsetMs = sample;
        }
        State = state;
        if (state is { Active: true, Audio: JamAudio.Guest })
        {
            // Titre absent de la bibliothèque : la recherche YouTube part dès la réception, sans attendre la boucle.
            var source = state.SourceTrackId ?? state.Track?.Title;
            if (source is not null && source != _playingSource) _ = YoutubeLookup(state, source);
            if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Mode invité : boucle de synchro. Le serveur ne pousse qu'aux changements (titre, pause, seek) ; entre deux, on
    /// extrapole la position de l'hôte et on compare en continu à celle du lecteur local.
    /// </summary>
    private async Task SyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (State is { Active: true, Audio: JamAudio.Guest } state) await SyncOnceAsync(state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Lecture impossible pour l'instant (réseau, lecteur, YouTube…) : on retente au tour suivant plutôt
                // que de laisser une erreur imprévue arrêter la synchro pour tout le Jam.
            }
            try
            {
                await _wake.WaitAsync(SyncTick, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Lecture/pause, au plus une fois par <see cref="ToggleSettle"/> : l'état du lecteur ne suit la commande qu'après
    /// un aller-retour, et basculer de nouveau entre-temps annulerait la première.
    /// </summary>
    private async Task TogglePlayAsync()
    {
        if (NowMs - _lastToggleAtMs < ToggleSettle.TotalMilliseconds) return;
        _lastToggleAtMs = NowMs;
        await _player.TogglePlayAsync();
    }

    private static readonly TimeSpan ToggleSettle = TimeSpan.FromMilliseconds(1500);
    private long _lastToggleAtMs;

    private async Task SyncOnceAsync(JamStateDto state)
    {
        // Identité du titre de l'hôte (Spotify) : c'est elle qui décide d'un changement, pas ce qu'on joue pour lui.
        var source = state.SourceTrackId ?? state.Resolved?.Id.ToString() ?? state.Track?.Title;
        if (source is null)
        {
            // L'hôte ne joue pas de titre (pub, épisode, rien) : on ne laisse pas tourner le précédent à contretemps.
            _playingSource = null;
            if (_playing is { } last && _player.State.Current?.Id == last.Id && _player.State.IsPlaying) await TogglePlayAsync();
            return;
        }
        if (_playingSource != source)
        {
            if (await PickTrackAsync(state, source) is not { } pick) return; // rien de jouable pour l'instant
            await StartAsync(state, source, pick);
            return;
        }
        if (_playing is not { } track) return;
        if (_player.State.Current?.Id != track.Id)
        {
            // L'utilisateur joue autre chose / chargement en cours : jamais de vitesse modifiée hors synchro.
            await SetSpeedAsync(1);
            return;
        }

        // Titre suivant prêt mais pas encore dans la file : on l'ajoute juste après, le lecteur le pré-charge.
        if (state.Upcoming is [var next, ..] && !_player.State.Queue.Skip(_player.State.CurrentIndex + 1).Any(t => t.Id == next.Id))
        {
            await _player.EnqueueAsync([next], playNext: true);
            return;
        }

        if (state.IsPlaying != _player.State.IsPlaying && !_player.State.IsBuffering)
        {
            await TogglePlayAsync();
            return;
        }
        if (!state.IsPlaying || _player.State.IsBuffering || !_playerRunning)
        {
            await SetSpeedAsync(1);
            return;
        }

        var now = NowMs;
        if (now - _lastSeekAtMs < SeekSettle.TotalMilliseconds) return;

        var host = HostPosition(state);
        var drift = PlayerPositionNow() - host; // > 0 : on est en avance
        // Mesure l'effet du dernier recalage pour apprendre le temps de reprise du lecteur.
        if (!double.IsNaN(_lastSeekTarget))
        {
            if (_lastSeekCold) _coldLeadSeconds = Math.Clamp(_coldLeadSeconds - drift * 0.6, 0, 4);
            else _warmLeadSeconds = Math.Clamp(_warmLeadSeconds - drift * 0.6, 0, 1.5);
            _lastSeekTarget = double.NaN;
        }
        // Fin de titre : l'hôte va passer au suivant, inutile de se battre sur les dernières secondes.
        if (state.Track is { DurationSeconds: > 0 } t && host > t.DurationSeconds - 1.5)
        {
            await SetSpeedAsync(1);
            return;
        }
        if (Math.Abs(drift) > DriftSeconds)
        {
            // Trop loin pour rattraper en douceur : un seul saut.
            await SetSpeedAsync(1);
            await SeekAsync(host, cold: false);
            return;
        }
        // Petit écart : on accélère ou ralentit légèrement (hystérésis pour ne pas osciller autour de zéro).
        var nudging = Math.Abs(_speed - 1) > 0.001;
        if (Math.Abs(drift) > NudgeStartSeconds || (nudging && Math.Abs(drift) > NudgeStopSeconds))
            await SetSpeedAsync(1 - Math.Clamp(drift * NudgeGain, -MaxNudge, MaxNudge));
        else
            await SetSpeedAsync(1);
    }

    /// <summary>
    /// Ce qu'on joue pour le titre de l'hôte : le titre de la bibliothèque s'il est prêt, sinon directement le flux
    /// YouTube (≈ 0,5 s) pendant que le serveur l'importe. Une fois lancé, on ne bascule plus en cours de titre.
    /// </summary>
    private async Task<TrackDto?> PickTrackAsync(JamStateDto state, string source)
    {
        if (state is { Resolve: JamResolve.Matched, Resolved: { } library }) return library;
        if (YoutubeLookup(state, source) is not { } lookup) return null;
        var audio = await lookup;
        if (audio is null || _playingSource == source) return null;
        var meta = state.Track!;
        return YoutubeTracks.Create(audio.VideoId, meta.Title, meta.Artist, meta.DurationSeconds > 0 ? meta.DurationSeconds : audio.DurationSeconds);
    }

    /// <summary>Recherche YouTube du titre de l'hôte, lancée une fois par titre (dès la réception de l'état).</summary>
    private Task<YoutubeAudio?>? YoutubeLookup(JamStateDto state, string source)
    {
        if (state is not { Audio: JamAudio.Guest, Track: { Title.Length: > 0 } meta }) return null;
        if (state.Resolve == JamResolve.Matched) return null;
        lock (_lookupGate)
        {
            if (_lookupSource != source)
            {
                _lookupSource = source;
                _lookup = _youtube.FindAsync(meta.Title, meta.Artist, meta.DurationSeconds);
            }
            return _lookup;
        }
    }

    private async Task StartAsync(JamStateDto state, string source, TrackDto track)
    {
        _playingSource = source;
        _playing = track;
        _lastSeekTarget = double.NaN;
        await SetSpeedAsync(1);
        var queue = _player.State.Queue;
        var at = -1;
        for (var i = Math.Max(0, _player.State.CurrentIndex); i < queue.Count; i++)
            if (queue[i].Id == track.Id) { at = i; break; }
        if (at == _player.State.CurrentIndex && at >= 0)
        {
            // Déjà enchaîné tout seul (titre suivant pré-chargé) : rien à relancer, on laisse jouer.
        }
        else if (at >= 0)
        {
            // Pré-chargé dans la file : on y saute (déjà en mémoire tampon, démarrage immédiat).
            await _player.JumpToAsync(at);
            await SeekAsync(HostPosition(state), cold: false);
        }
        else
        {
            // Nouveau titre : chargé directement à la position de l'hôte (une seule requête, pas « 0 puis saut »),
            // plus l'avance de démarrage apprise.
            var target = Math.Max(0, HostPosition(state) + _coldLeadSeconds);
            _lastSeekCold = true;
            _lastSeekAtMs = NowMs;
            _lastSeekTarget = target;
            await _player.PlayAtAsync([track, .. state.Upcoming ?? []], 0, target);
        }
        if (!state.IsPlaying && _player.State.IsPlaying) await _player.TogglePlayAsync();
    }

    /// <summary>Recale le lecteur, en visant où sera l'hôte quand le lecteur aura repris.</summary>
    private async Task SeekAsync(double hostPosition, bool cold)
    {
        _lastSeekCold = cold;
        var target = Math.Max(0, hostPosition + (cold ? _coldLeadSeconds : _warmLeadSeconds));
        _lastSeekAtMs = NowMs;
        _lastSeekTarget = target;
        await _player.SeekAsync(target);
    }

    /// <summary>Position de l'hôte maintenant, dans l'horloge de l'appareil (décalage serveur et temps de vol compensés).</summary>
    private double HostPosition(JamStateDto state)
    {
        var pos = state.PositionSeconds;
        if (state.IsPlaying && state.AsOfUnixMs > 0)
        {
            var serverNow = NowMs - (_serverClockOffsetMs ?? 0);
            pos += Math.Max(0, serverNow - state.AsOfUnixMs) / 1000.0;
        }
        if (state.Track is { DurationSeconds: > 0 } t) pos = Math.Min(pos, t.DurationSeconds);
        return Math.Max(0, pos);
    }

    private static JamStateDto? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JamStateDto>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _player.StateChanged -= OnPlayerState;
        StopFollow();
    }
}
