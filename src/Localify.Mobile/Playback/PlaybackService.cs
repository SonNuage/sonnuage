using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.DataSource.Cache;
using AndroidX.Media3.Database;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.Extractor;
using AndroidX.Media3.Session;
using Google.Common.Util.Concurrent;
using Localify.Contracts;
using Localify.UI.Services;
using static AndroidX.Media3.Session.MediaLibraryService;

namespace Localify.Mobile.Playback;

/// <summary>
/// Service de lecture natif : ExoPlayer + MediaLibrarySession. Il porte la notification, les contrôles écran verrouillé
/// et casque, et l'arborescence Android Auto. L'interface Blazor le pilote via un MediaController (<see cref="Services.AndroidPlayer"/>).
/// <list type="bullet">
/// <item>Cache disque LRU de 1 Go : une piste déjà écoutée ou pré-chargée ne repasse plus par le réseau.</item>
/// <item>Pré-chargement des N pistes suivantes en entier dans ce cache (tunnels, zones blanches en voiture).</item>
/// <item>Tampon de lecture de 5 minutes.</item>
/// </list>
/// </summary>
[Service(Exported = true, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(["androidx.media3.session.MediaLibraryService", "android.media.browse.MediaBrowserService"])]
public sealed class PlaybackService : MediaLibraryService
{
    private const long CacheBytes = 1024L * 1024 * 1024;
    private static readonly object CacheLock = new();
    private static SimpleCache? _cache;

    private MediaLibrarySession? _session;
    private Prefetcher? _prefetcher;
    private AccountStore? _accounts;
    private QueueStore? _queue;

    /// <summary>Clé de sauvegarde de la file actuellement chargée (celle du compte à qui elle appartient).</summary>
    private string? _queueKey;

    public override void OnCreate()
    {
        base.OnCreate();
        var services = IPlatformApplication.Current!.Services;
        var api = services.GetRequiredService<ApiClient>();
        var settings = services.GetRequiredService<PlaybackSettings>();
        var browser = services.GetRequiredService<LibraryBrowser>();
        var queue = _queue = services.GetRequiredService<QueueStore>();
        var accounts = _accounts = services.GetRequiredService<AccountStore>();
        _queueKey = queue.CurrentKey;
        var cache = Cache(this);

        var http = new DefaultHttpDataSource.Factory()
            .SetUserAgent("Localify-Android")!
            .SetConnectTimeoutMs(15_000)!
            .SetReadTimeoutMs(30_000)!
            .SetAllowCrossProtocolRedirects(true)!;
        var cached = new CacheDataSource.Factory()
            .SetCache(cache)!
            .SetUpstreamDataSourceFactory(http)!
            .SetFlags(CacheDataSource.FlagIgnoreCacheOnError)!;
        var resolver = new StreamResolver(api, settings, accounts, cache);
        var extractors = new DefaultExtractorsFactory()
            .SetConstantBitrateSeekingEnabled(true)!
            .SetConstantBitrateSeekingAlwaysEnabled(true)!;
        var loadControl = new DefaultLoadControl.Builder()
            .SetBufferDurationsMs(30_000, 300_000, 1_500, 3_000)!
            .Build();
        var audio = new AudioAttributes.Builder()
            .SetUsage(C.UsageMedia)!
            .SetContentType(C.AudioContentTypeMusic)!
            .Build();

        var exo = new ExoPlayerBuilder(this)
            .SetMediaSourceFactory(new DefaultMediaSourceFactory(new ResolvingDataSource.Factory(cached, resolver), extractors))!
            .SetLoadControl(loadControl)!
            .SetAudioAttributes(audio, true)!
            .SetHandleAudioBecomingNoisy(true)!
            .SetWakeMode(C.WakeModeNetwork)!
            .Build()!;
        exo.ShuffleOrder = new ShuffleOrderUnshuffledShuffleOrder(0);
        var player = new ShufflingPlayer(exo);

        _prefetcher = new Prefetcher(cached, resolver, settings);
        player.AddListener(new PlayerEvents(player, _prefetcher, queue, () => _queueKey, api));

        var activity = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        _session = new MediaLibrarySession.Builder(this, player, new LibraryCallback(browser, queue))
            .SetSessionActivity(activity!)!
            .Build();
        accounts.ActiveChanged += OnAccountChanged;
    }

    /// <summary>
    /// Changement de compte : la file du compte quitté est sauvée sous sa clé, la lecture s'arrête, puis la file du
    /// nouveau compte est chargée (sans lecture) et Android Auto est invité à recharger son arborescence.
    /// </summary>
    private void OnAccountChanged() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (_session?.Player is not { } player || _queue is not { } queue) return;
        queue.Save(player, _queueKey);
        _queueKey = null;
        _prefetcher?.Cancel();
        player.Stop();
        player.ClearMediaItems();
        _queueKey = queue.CurrentKey;
        foreach (var id in new[] { BrowseIds.Root, BrowseIds.Playlists, BrowseIds.Albums, BrowseIds.Artists, BrowseIds.Recent })
        {
            _session.NotifyChildrenChanged(id, int.MaxValue, null);
        }
        var saved = await queue.LoadAsync();
        if (saved is null || _session?.Player is not { } current || current.MediaItemCount > 0) return;
        current.RepeatMode = saved.Value.RepeatMode;
        current.SetMediaItems(saved.Value.Items, saved.Value.Index, saved.Value.PositionMs);
        current.Prepare();
    });

    public override MediaLibrarySession? OnGetSessionFromMediaLibraryService(MediaSession.ControllerInfo? controllerInfo) => _session;

    // Le binding expose aussi la méthode de la classe parente comme abstraite.
    public override MediaSession? OnGetSession(MediaSession.ControllerInfo? controllerInfo) => _session;

    public override void OnDestroy()
    {
        if (_accounts is not null) _accounts.ActiveChanged -= OnAccountChanged;
        _prefetcher?.Cancel();
        if (_session is not null)
        {
            _session.Player?.Release();
            _session.Release();
            _session = null;
        }
        base.OnDestroy();
    }

    /// <summary>Un seul SimpleCache par dossier et par processus : le service peut être recréé, le cache non.</summary>
    private static SimpleCache Cache(Context context)
    {
        lock (CacheLock)
        {
            return _cache ??= new SimpleCache(
                new Java.IO.File(context.CacheDir, "audio"),
                new LeastRecentlyUsedCacheEvictor(CacheBytes),
                new StandaloneDatabaseProvider(context));
        }
    }

    /// <summary>Événements du lecteur : pré-chargement, historique d'écoute, sauvegarde de la file.</summary>
    private sealed class PlayerEvents(Media3IPlayer player, Prefetcher prefetcher, QueueStore queue, Func<string?> queueKey, ApiClient api) : Java.Lang.Object, IPlayerListener
    {
        private const double PlayThresholdSeconds = 30;

        public void OnTimelineChanged(Timeline? timeline, int reason) => prefetcher.Schedule(player);

        public void OnMediaItemTransition(MediaItem? mediaItem, int reason)
        {
            prefetcher.Schedule(player);
            queue.Save(player, queueKey());
        }

        public void OnRepeatModeChanged(int repeatMode) => prefetcher.Schedule(player);

        public void OnIsPlayingChanged(bool isPlaying)
        {
            if (!isPlaying) queue.Save(player, queueKey());
        }

        public void OnPositionDiscontinuity(PlayerPositionInfo? oldPosition, PlayerPositionInfo? newPosition, int reason)
        {
            if (oldPosition is null || newPosition is null || oldPosition.MediaItemIndex == newPosition.MediaItemIndex && reason != PlayerConst.DiscontinuityAutoTransition) return;
            Report(oldPosition.MediaItem, oldPosition.PositionMs / 1000.0, reason == PlayerConst.DiscontinuityAutoTransition);
        }

        public void OnPlaybackStateChanged(int playbackState)
        {
            if (playbackState == PlayerConst.StateEnded) Report(player.CurrentMediaItem, player.CurrentPosition / 1000.0, completed: true);
        }

        /// <summary>Même règle que le web : une écoute compte au-delà de 30 s (ou de la moitié d'une piste courte).</summary>
        private void Report(MediaItem? item, double seconds, bool completed)
        {
            // Pas de clé : la file est en cours de vidage pour un changement de compte, l'écoute n'est pas à imputer.
            if (queueKey() is null || TrackItems.ToTrack(item) is not { } track) return;
            if (!completed && seconds < Math.Min(PlayThresholdSeconds, track.DurationSeconds * 0.5)) return;
            var played = completed ? track.DurationSeconds : seconds;
            _ = Task.Run(async () =>
            {
                try
                {
                    await api.ReportPlayAsync(new PlayEventRequest(track.Id, played, completed));
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException)
                {
                }
            });
        }
    }

    /// <summary>Arborescence Android Auto, recherche, résolution des éléments demandés et reprise de lecture.</summary>
    private sealed class LibraryCallback(LibraryBrowser browser, QueueStore queue) : Java.Lang.Object, MediaLibrarySession.ICallback
    {
        private const int ErrorNotSupported = -6;
        private readonly Dictionary<string, IList<MediaItem>> _searches = [];

        public IListenableFuture? OnGetLibraryRoot(MediaLibrarySession? session, MediaSession.ControllerInfo? browserInfo, LibraryParams? @params) =>
            FutureExtensions.Completed(LibraryResult.OfItem(browser.RootItem(), @params));

        public IListenableFuture? OnGetItem(MediaLibrarySession? session, MediaSession.ControllerInfo? browserInfo, string? mediaId) =>
            FutureExtensions.Completed(LibraryResult.OfError(ErrorNotSupported));

        public IListenableFuture? OnGetChildren(MediaLibrarySession? session, MediaSession.ControllerInfo? browserInfo, string? parentId, int page, int pageSize, LibraryParams? @params) =>
            FutureExtensions.FromTask(async () => LibraryResult.OfItemList(Page(await browser.ChildrenAsync(parentId ?? BrowseIds.Root), page, pageSize), @params)!);

        public IListenableFuture? OnSearch(MediaLibrarySession? session, MediaSession.ControllerInfo? browserInfo, string? query, LibraryParams? @params) =>
            FutureExtensions.FromTask(async () =>
            {
                var results = await browser.SearchAsync(query ?? "");
                lock (_searches) _searches[query ?? ""] = results;
                MainThread.BeginInvokeOnMainThread(() => session?.NotifySearchResultChanged(browserInfo, query, results.Count, @params));
                return LibraryResult.OfVoid()!;
            });

        public IListenableFuture? OnGetSearchResult(MediaLibrarySession? session, MediaSession.ControllerInfo? browserInfo, string? query, int page, int pageSize, LibraryParams? @params) =>
            FutureExtensions.FromTask(async () =>
            {
                IList<MediaItem>? results;
                lock (_searches) _searches.TryGetValue(query ?? "", out results);
                results ??= await browser.SearchAsync(query ?? "");
                return LibraryResult.OfItemList(Page(results, page, pageSize), @params)!;
            });

        public IListenableFuture? OnSetMediaItems(MediaSession? mediaSession, MediaSession.ControllerInfo? controller, IList<MediaItem>? mediaItems, int startIndex, long startPositionMs) =>
            FutureExtensions.FromTask(async () =>
            {
                var (items, index) = await browser.ResolveAsync(mediaItems ?? [], startIndex);
                return new MediaSession.MediaItemsWithStartPosition(items, index, startPositionMs);
            });

        public IListenableFuture? OnAddMediaItems(MediaSession? mediaSession, MediaSession.ControllerInfo? controller, IList<MediaItem>? mediaItems) =>
            FutureExtensions.FromTask(async () =>
            {
                var (items, _) = await browser.ResolveAsync(mediaItems ?? [], 0);
                return (Java.Lang.Object)new JavaList<MediaItem>(items);
            });

        public IListenableFuture? OnPlaybackResumption(MediaSession? mediaSession, MediaSession.ControllerInfo? controller) =>
            FutureExtensions.FromTask(async () =>
            {
                var saved = await queue.LoadAsync() ?? throw new InvalidOperationException("Aucune file à reprendre.");
                return new MediaSession.MediaItemsWithStartPosition(saved.Items, saved.Index, saved.PositionMs);
            });

        private static List<MediaItem> Page(IList<MediaItem> items, int page, int pageSize) =>
            pageSize <= 0 ? items.ToList() : items.Skip(page * pageSize).Take(pageSize).ToList();
    }
}

/// <summary>
/// Télécharge entièrement dans le cache les N pistes qui suivent, l'une après l'autre.
/// Une coupure réseau en cours de piste ne s'entend donc pas tant que le cache tient.
/// </summary>
public sealed class Prefetcher(CacheDataSource.Factory cacheFactory, StreamResolver resolver, PlaybackSettings settings)
{
    private CancellationTokenSource? _cts;
    private CacheWriter? _writer;

    /// <summary>À appeler sur le thread du lecteur.</summary>
    public void Schedule(Media3IPlayer player)
    {
        var uris = new List<Android.Net.Uri>();
        var count = player.MediaItemCount;
        var index = player.CurrentMediaItemIndex;
        for (var step = 1; step <= settings.PrefetchCount && count > 0 && index >= 0; step++)
        {
            var next = index + step;
            if (next >= count)
            {
                if (player.RepeatMode != PlayerConst.RepeatAll) break;
                next %= count;
            }
            if (next == index) break;
#pragma warning disable CS0618 // localConfiguration n'est exposé par le binding que sous son ancien nom.
            if (player.GetMediaItemAt(next)?.PlaybackProperties?.Uri is { } uri) uris.Add(uri);
#pragma warning restore CS0618
        }

        Cancel();
        if (uris.Count == 0) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            foreach (var uri in uris)
            {
                if (cts.IsCancellationRequested) return;
                try
                {
                    var spec = resolver.ResolveDataSpec(new DataSpec(uri));
                    var writer = _writer = new CacheWriter(cacheFactory.CreateDataSourceForDownloading(), spec, null, null);
                    if (cts.IsCancellationRequested) return;
                    writer.Cache();
                }
                catch (Exception)
                {
                    // Réseau absent, annulation, session expirée : la lecture normale prendra le relais.
                }
            }
        });
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _writer?.Cancel();
        _writer = null;
    }
}
