using System.Text.Json;
using Android.OS;
using AndroidX.Media3.Common;
using Google.Common.Util.Concurrent;
using Java.Util.Concurrent;
using Localify.Contracts;
using Localify.UI.Services;

namespace Localify.Mobile.Playback;

/// <summary>Conversion TrackDto ⇄ MediaItem. La piste complète voyage en JSON dans les extras des métadonnées.</summary>
public static class TrackItems
{
    public const string Scheme = "localify";
    private const string TrackExtra = "localify.track";

    public static MediaItem From(TrackDto track, string? artworkUrl)
    {
        var extras = new Bundle();
        extras.PutString(TrackExtra, JsonSerializer.Serialize(track, JsonDefaults.Options));
        var metadata = new MediaMetadata.Builder()
            .SetTitle(track.Title)!
            .SetArtist(track.ArtistName)!
            .SetAlbumTitle(track.AlbumTitle)!
            .SetArtworkUri(artworkUrl is null ? null : Android.Net.Uri.Parse(artworkUrl))!
            .SetDurationMs(Java.Lang.Long.ValueOf((long)(track.DurationSeconds * 1000)))!
            .SetIsBrowsable(Java.Lang.Boolean.False)!
            .SetIsPlayable(Java.Lang.Boolean.True)!
            .SetMediaType(Java.Lang.Integer.ValueOf(MediaMetadata.MediaTypeMusic))!
            .SetExtras(extras)!
            .Build();
        return new MediaItem.Builder()
            .SetMediaId(BrowseIds.Track(track.Id))!
            .SetUri(StreamUri(track.Id))!
            .SetMediaMetadata(metadata)!
            .Build()!;
    }

    /// <summary>URI interne, résolue au moment du chargement (qualité selon le réseau, jeton média frais).</summary>
    public static Android.Net.Uri StreamUri(Guid trackId) => Android.Net.Uri.Parse($"{Scheme}://track/{trackId:N}")!;

    public static Guid? TrackIdFromUri(Android.Net.Uri? uri) =>
        uri?.Scheme == Scheme && uri.Host == "track" && Guid.TryParse(uri.LastPathSegment, out var id) ? id : null;

    public static TrackDto? ToTrack(MediaItem? item)
    {
        var raw = item?.MediaMetadata?.Extras?.GetString(TrackExtra);
        if (raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<TrackDto>(raw, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Recherche vocale (« Ok Google, mets … ») : le champ requestMetadata n'est pas exposé par le binding.</summary>
    public static string? SearchQuery(MediaItem item)
    {
        var field = item.JniPeerMembers.InstanceFields.GetObjectValue("requestMetadata.Landroidx/media3/common/MediaItem$RequestMetadata;", item);
        return Java.Lang.Object.GetObject<MediaItem.RequestMetadata>(field.Handle, Android.Runtime.JniHandleOwnership.TransferLocalRef)?.SearchQuery;
    }

    public static List<MediaItem> Items(this Media3IPlayer player)
    {
        var items = new List<MediaItem>(player.MediaItemCount);
        for (var i = 0; i < player.MediaItemCount; i++) items.Add(player.GetMediaItemAt(i)!);
        return items;
    }
}

/// <summary>Constantes de androidx.media3.common.Player (non exposées proprement par le binding).</summary>
public static class PlayerConst
{
    public const int StateIdle = 1;
    public const int StateBuffering = 2;
    public const int StateReady = 3;
    public const int StateEnded = 4;
    public const int RepeatOff = 0;
    public const int RepeatOne = 1;
    public const int RepeatAll = 2;
    public const int DiscontinuityAutoTransition = 0;
}

public static class FutureExtensions
{
    public static IListenableFuture Completed(Java.Lang.Object? value)
    {
        var future = new TaskFuture();
        future.SetResult(value);
        return future;
    }

    /// <summary>Exécute un calcul .NET asynchrone et le publie comme ListenableFuture pour Media3.</summary>
    public static IListenableFuture FromTask<T>(Func<Task<T>> work) where T : Java.Lang.Object
    {
        var future = new TaskFuture();
        _ = Task.Run(async () =>
        {
            try
            {
                future.SetResult(await work());
            }
            catch (Exception ex)
            {
                future.SetException(ex);
            }
        });
        return future;
    }

    public static Task<Java.Lang.Object?> AsTask(this IListenableFuture future)
    {
        var tcs = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                tcs.TrySetResult(future.Get());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }), DirectExecutor.Instance);
        return tcs.Task;
    }

    private sealed class DirectExecutor : Java.Lang.Object, IExecutor
    {
        public static readonly DirectExecutor Instance = new();
        public void Execute(Java.Lang.IRunnable? command) => command?.Run();
    }
}

/// <summary>
/// ListenableFuture minimal adossé à une Task (le binding Guava n'expose pas SettableFuture).
/// Media3 n'utilise que addListener + get une fois le résultat disponible.
/// </summary>
public sealed class TaskFuture : Java.Lang.Object, IListenableFuture
{
    private readonly TaskCompletionSource<Java.Lang.Object?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(Java.Lang.IRunnable Runnable, IExecutor Executor)> _listeners = [];

    public bool IsCancelled => _tcs.Task.IsCanceled;
    public bool IsDone => _tcs.Task.IsCompleted;

    public void SetResult(Java.Lang.Object? value)
    {
        if (_tcs.TrySetResult(value)) Fire();
    }

    public void SetException(Exception error)
    {
        if (_tcs.TrySetException(error)) Fire();
    }

    public bool Cancel(bool mayInterruptIfRunning)
    {
        if (!_tcs.TrySetCanceled()) return false;
        Fire();
        return true;
    }

    public void AddListener(Java.Lang.IRunnable? listener, IExecutor? executor)
    {
        if (listener is null || executor is null) return;
        lock (_listeners)
        {
            if (!IsDone)
            {
                _listeners.Add((listener, executor));
                return;
            }
        }
        executor.Execute(listener);
    }

    public Java.Lang.Object? Get()
    {
        try
        {
            return _tcs.Task.GetAwaiter().GetResult();
        }
        catch (TaskCanceledException)
        {
            throw new CancellationException();
        }
        catch (Exception ex)
        {
            throw new ExecutionException(ex as Java.Lang.Throwable ?? new Java.Lang.RuntimeException(ex.Message));
        }
    }

    public Java.Lang.Object? Get(long timeout, TimeUnit? unit)
    {
        var ms = unit?.ToMillis(timeout) ?? timeout;
        if (!((IAsyncResult)_tcs.Task).AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(ms))) throw new Java.Util.Concurrent.TimeoutException();
        return Get();
    }

    private void Fire()
    {
        List<(Java.Lang.IRunnable Runnable, IExecutor Executor)> pending;
        lock (_listeners)
        {
            pending = [.. _listeners];
            _listeners.Clear();
        }
        foreach (var (runnable, executor) in pending) executor.Execute(runnable);
    }
}
