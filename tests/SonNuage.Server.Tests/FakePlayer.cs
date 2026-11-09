using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Lecteur local minimal : la file et l'état, sans audio.</summary>
internal sealed class FakePlayer : IPlayer, IRestorablePlayer, IAdoptablePlayer
{
    public bool SupportsCrossfade { get; init; }

    private List<TrackDto> _queue = [];

    public PlayerState State { get; private set; } = PlayerState.Empty;
    public bool Restored { get; private set; }
    public event Action? StateChanged;

    private Task Set(PlayerState s)
    {
        State = s;
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task RestoreAsync()
    {
        Restored = true;
        return Task.CompletedTask;
    }

    public Task AdoptAsync(PlayerState state, bool autoplay)
    {
        _queue = state.Queue.ToList();
        return Set(State with { Queue = _queue, CurrentIndex = state.CurrentIndex, PositionSeconds = state.PositionSeconds, IsPlaying = autoplay });
    }

    public Task ReleaseAsync() => Set(State with { IsPlaying = false });

    /// <summary>Radio thématique lancée par <see cref="PlayStationAsync"/> ; null après une lecture ordinaire.</summary>
    public string? Station { get; private set; }

    public async Task PlayStationAsync(string stationId, IReadOnlyList<TrackDto> tracks)
    {
        await PlayAsync(tracks);
        Station = stationId;
    }

    public Task PlayAsync(IReadOnlyList<TrackDto> tracks, int startIndex = 0, bool? shuffle = null)
    {
        Station = null;
        _queue = tracks.ToList();
        return Set(State with { Queue = _queue, CurrentIndex = startIndex, IsPlaying = true, PositionSeconds = 0 });
    }

    public Task TogglePlayAsync() => Set(State with { IsPlaying = !State.IsPlaying });
    public Task NextAsync() => Set(State with { CurrentIndex = Math.Min(State.CurrentIndex + 1, _queue.Count - 1), PositionSeconds = 0 });
    public Task PreviousAsync() => Set(State with { CurrentIndex = Math.Max(State.CurrentIndex - 1, 0), PositionSeconds = 0 });
    public Task SeekAsync(double seconds) => Set(State with { PositionSeconds = seconds });
    public Task JumpToAsync(int queueIndex) => Set(State with { CurrentIndex = queueIndex });
    public Task SetShuffleAsync(bool shuffle) => Set(State with { Shuffle = shuffle });
    public Task SetRepeatAsync(RepeatMode mode) => Set(State with { Repeat = mode });
    public Task SetVolumeAsync(double volume) => Set(State with { Volume = volume });

    public Task EnqueueAsync(IReadOnlyList<TrackDto> tracks, bool playNext)
    {
        _queue = [.. _queue, .. tracks];
        return Set(State with { Queue = _queue });
    }

    public Task RemoveAtAsync(int queueIndex)
    {
        _queue = _queue.Where((_, i) => i != queueIndex).ToList();
        return Set(State with { Queue = _queue });
    }

    public Task ReloadSettingsAsync() => Task.CompletedTask;
    public void UpdateTrack(TrackDto track) { }
}
