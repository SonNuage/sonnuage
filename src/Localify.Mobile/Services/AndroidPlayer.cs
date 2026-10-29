using Android.Content;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.Session;
using Localify.Contracts;
using Localify.Mobile.Playback;
using Localify.UI.Services;
using IPlayer = Localify.UI.Services.IPlayer;

namespace Localify.Mobile.Services;

/// <summary>
/// Lecteur de l'interface Blazor sur Android : simple télécommande (MediaController) du <see cref="PlaybackService"/>.
/// La file vit dans le service, ce qui la partage avec la notification et Android Auto ; l'état exposé ici est
/// relu depuis le contrôleur à chaque événement. Le MediaController n'est utilisable que depuis le thread principal.
/// </summary>
public sealed class AndroidPlayer(QueueStore queueStore, PlaybackSettings settings, LibraryBrowser browser) : IPlayer, IRestorablePlayer
{
    private MediaController? _controller;
    private Task<MediaController>? _connecting;
    private PlayerState _state = PlayerState.Empty;
    private IReadOnlyList<TrackDto> _queue = [];
    private readonly Dictionary<Guid, TrackDto> _updated = [];
    private IDispatcherTimer? _ticker;

    public PlayerState State => _state;
    public event Action? StateChanged;

    public Task RestoreAsync() => OnMain(async c =>
    {
        if (c.MediaItemCount > 0) return;
        if (await queueStore.LoadAsync() is not { } saved) return;
        c.RepeatMode = saved.RepeatMode;
        c.SetMediaItems(saved.Items, saved.Index, saved.PositionMs);
        c.Prepare();
    });

    public Task PlayAsync(IReadOnlyList<TrackDto> tracks, int startIndex = 0, bool? shuffle = null) => OnMain(c =>
    {
        if (tracks.Count == 0) return;
        var start = shuffle == true && startIndex < 0 ? Random.Shared.Next(tracks.Count) : Math.Clamp(startIndex, 0, tracks.Count - 1);
        // Le service mélange à la réception si le shuffle est actif : on fixe le mode avant d'envoyer la file.
        if (shuffle is { } s && c.ShuffleModeEnabled != s) c.ShuffleModeEnabled = s;
        c.SetMediaItems(tracks.Select(browser.Item).ToList(), start, 0);
        c.Prepare();
        c.Play();
    });

    public Task TogglePlayAsync() => OnMain(c =>
    {
        if (c.IsPlaying)
        {
            c.Pause();
            return;
        }
        if (c.PlaybackState == PlayerConst.StateIdle) c.Prepare();
        if (c.PlaybackState == PlayerConst.StateEnded) c.SeekTo(0, 0);
        c.Play();
    });

    public Task NextAsync() => OnMain(c => c.SeekToNextMediaItem());

    /// <summary>Media3 applique la règle habituelle : au-delà de 3 s, retour au début de la piste.</summary>
    public Task PreviousAsync() => OnMain(c => c.SeekToPrevious());

    public Task SeekAsync(double seconds) => OnMain(c => c.SeekTo((long)(seconds * 1000)));

    public Task JumpToAsync(int queueIndex) => OnMain(c =>
    {
        c.SeekTo(queueIndex, 0);
        c.Play();
    });

    public Task SetShuffleAsync(bool shuffle) => OnMain(c => c.ShuffleModeEnabled = shuffle);

    public Task SetRepeatAsync(RepeatMode mode) => OnMain(c => c.RepeatMode = mode switch
    {
        RepeatMode.All => PlayerConst.RepeatAll,
        RepeatMode.One => PlayerConst.RepeatOne,
        _ => PlayerConst.RepeatOff,
    });

    public Task SetVolumeAsync(double volume) => OnMain(c => c.Volume = (float)Math.Clamp(volume, 0, 1));

    public Task EnqueueAsync(IReadOnlyList<TrackDto> tracks, bool playNext)
    {
        if (_state.Current is null) return PlayAsync(tracks);
        return OnMain(c => c.AddMediaItems(playNext ? c.CurrentMediaItemIndex + 1 : c.MediaItemCount, tracks.Select(browser.Item).ToList()));
    }

    public Task RemoveAtAsync(int queueIndex) => OnMain(c => c.RemoveMediaItem(queueIndex));

    /// <summary>Le service partage le même PlaybackSettings (même processus) : la nouvelle qualité vaut dès la piste suivante.</summary>
    public Task ReloadSettingsAsync() => settings.LoadAsync();

    public void UpdateTrack(TrackDto track)
    {
        _updated[track.Id] = track;
        _queue = _queue.Select(t => t.Id == track.Id ? track : t).ToList();
        _state = _state with { Queue = _queue };
        StateChanged?.Invoke();
    }

    private Task OnMain(Action<MediaController> action) => OnMain(c =>
    {
        action(c);
        return Task.CompletedTask;
    });

    private Task OnMain(Func<MediaController, Task> action) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        try
        {
            await action(await ControllerAsync());
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            _state = _state with { Error = ex.Message };
            StateChanged?.Invoke();
        }
    });

    private Task<MediaController> ControllerAsync()
    {
        if (_controller is not null) return Task.FromResult(_controller);
        return _connecting ??= ConnectAsync();
    }

    private async Task<MediaController> ConnectAsync()
    {
        var context = Android.App.Application.Context;
        var token = new SessionToken(context, new ComponentName(context, Java.Lang.Class.FromType(typeof(PlaybackService))));
        var result = await new MediaController.Builder(context, token).BuildAsync()!.AsTask();
        var controller = result.JavaCast<MediaController>()!;
        controller.AddListener(new Listener(this));
        _controller = controller;
        _ticker = Application.Current!.Dispatcher.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(500);
        _ticker.Tick += (_, _) => Refresh(timelineChanged: false);
        Refresh(timelineChanged: true);
        return controller;
    }

    private void Refresh(bool timelineChanged)
    {
        var c = _controller;
        if (c is null) return;
        if (timelineChanged)
        {
            _queue = c.Items()
                .Select(TrackItems.ToTrack)
                .OfType<TrackDto>()
                .Select(t => _updated.GetValueOrDefault(t.Id, t))
                .ToList();
        }
        var index = c.CurrentMediaItemIndex;
        var current = index >= 0 && index < _queue.Count ? _queue[index] : null;
        _state = new PlayerState(
            _queue,
            current is null ? -1 : index,
            c.IsPlaying,
            c.PlayWhenReady && c.PlaybackState == PlayerConst.StateBuffering,
            Math.Max(0, c.CurrentPosition) / 1000.0,
            c.Duration > 0 ? c.Duration / 1000.0 : current?.DurationSeconds ?? 0,
            c.ShuffleModeEnabled,
            c.RepeatMode switch
            {
                PlayerConst.RepeatAll => RepeatMode.All,
                PlayerConst.RepeatOne => RepeatMode.One,
                _ => RepeatMode.Off,
            },
            c.Volume,
            c.PlayerError?.Message);

        if (_ticker is not null)
        {
            if (c.IsPlaying && !_ticker.IsRunning) _ticker.Start();
            else if (!c.IsPlaying && _ticker.IsRunning) _ticker.Stop();
        }
        StateChanged?.Invoke();
    }

    private sealed class Listener(AndroidPlayer owner) : Java.Lang.Object, IPlayerListener
    {
        public void OnEvents(Media3IPlayer? player, PlayerEvents? events)
        {
            if (events is null) return;
            owner.Refresh(events.Contains(EventTimelineChanged));
        }

        // androidx.media3.common.Player.EVENT_TIMELINE_CHANGED
        private const int EventTimelineChanged = 0;
    }
}
