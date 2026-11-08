using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Lecture et enregistrement des réglages de lecture du compte sur le serveur (implémenté par <see cref="ApiClient"/>).</summary>
public interface IPlaybackPreferencesApi
{
    Task<PlaybackPreferences?> PlaybackPreferencesAsync();
    Task<PlaybackPreferences?> SavePlaybackPreferencesAsync(PlaybackPreferences prefs);
}

/// <summary>
/// Réglages de lecture du compte actif (rechargés à chaque changement de compte).
/// <list type="bullet">
/// <item>Fondu et lecture infinie suivent le compte sur tous ses appareils : ils viennent du serveur, avec une copie
/// locale pour démarrer tout de suite et hors ligne. Un enregistrement fait hors ligne est renvoyé au chargement suivant.</item>
/// <item>Qualité et pré-chargement restent propres à l'appareil (données mobiles d'un téléphone, PC sur fibre…).</item>
/// </list>
/// </summary>
public sealed class PlaybackSettings
{
    private const string QualityKey = "sonnuage.quality";
    private const string MobileQualityKey = "sonnuage.quality.mobile";
    private const string PrefetchKey = "sonnuage.prefetch";
    private const string CrossfadeKey = "sonnuage.crossfade";
    private const string InfiniteKey = "sonnuage.infinite";
    private const string InfiniteAheadKey = "sonnuage.infinite.ahead";
    // Enregistrement pas encore parvenu au serveur : la copie locale fait foi jusqu'à son envoi.
    private const string PendingKey = "sonnuage.playback.pending";
    public const double MaxCrossfadeSeconds = PlaybackPreferences.MaxCrossfadeSeconds;

    public StreamQuality Quality { get; private set; } = StreamQuality.Normal;

    /// <summary>Qualité sur réseau mobile (Android). Plus basse par défaut.</summary>
    public StreamQuality MobileQuality { get; private set; } = StreamQuality.Low;

    /// <summary>Nombre de pistes suivantes téléchargées d'avance.</summary>
    public int PrefetchCount { get; private set; } = 2;

    /// <summary>Fondu enchaîné entre deux morceaux, de 0 (coupure nette) à 5 s, au dixième de seconde.</summary>
    public double CrossfadeSeconds { get; private set; }

    /// <summary>Lecture infinie : en fin de file, ajouter des morceaux proches (voir <see cref="Services.InfinitePlay"/>).</summary>
    public bool InfinitePlay { get; private set; }

    /// <summary>Morceaux gardés d'avance après celui qui joue, en lecture infinie.</summary>
    public int InfiniteAhead { get; private set; } = Services.InfinitePlay.DefaultAhead;

    private readonly IKeyValueStore _store;
    private readonly AccountStore _accounts;
    private readonly IPlaybackPreferencesApi _api;

    public PlaybackSettings(IKeyValueStore store, AccountStore accounts, IPlaybackPreferencesApi api)
    {
        _store = store;
        _accounts = accounts;
        _api = api;
        accounts.ActiveChanged += () => _ = LoadAsync();
    }

    public event Action? Changed;

    public async Task LoadAsync()
    {
        await _accounts.LoadAsync();
        var store = new ScopedStore(_store, _accounts);
        Quality = StreamQuality.Normal;
        MobileQuality = StreamQuality.Low;
        PrefetchCount = 2;
        CrossfadeSeconds = 0;
        InfinitePlay = false;
        InfiniteAhead = Services.InfinitePlay.DefaultAhead;
        if (double.TryParse(await store.GetAsync(CrossfadeKey), System.Globalization.CultureInfo.InvariantCulture, out var f)) CrossfadeSeconds = ClampFade(f);
        InfinitePlay = await store.GetAsync(InfiniteKey) == "1";
        if (int.TryParse(await store.GetAsync(InfiniteAheadKey), out var a)) InfiniteAhead = Math.Clamp(a, 1, Services.InfinitePlay.MaxAhead);
        if (await store.GetAsync(QualityKey) is { } rawQ && StreamQualities.TryParse(rawQ, out var q)) Quality = q;
        if (await store.GetAsync(MobileQualityKey) is { } rawM && StreamQualities.TryParse(rawM, out var m)) MobileQuality = m;
        if (int.TryParse(await store.GetAsync(PrefetchKey), out var p)) PrefetchCount = Math.Clamp(p, 0, 10);
        Changed?.Invoke();
        await SyncAsync(store);
    }

    /// <summary>Aligne fondu et lecture infinie sur le compte (ou lui envoie un enregistrement resté en attente).</summary>
    private async Task SyncAsync(ScopedStore store)
    {
        var account = _accounts.Active?.Id;
        var pending = await store.GetAsync(PendingKey) == "1";
        PlaybackPreferences? remote;
        try
        {
            remote = pending ? await _api.SavePlaybackPreferencesAsync(Current) : await _api.PlaybackPreferencesAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            return; // Hors ligne ou déconnecté : la copie locale reste en place.
        }
        // Réponse arrivée après un changement de compte : elle concerne l'ancien.
        if (remote is null || _accounts.Active?.Id != account) return;
        if (pending) await store.SetAsync(PendingKey, null);
        var prefs = remote.Clamped();
        if (prefs == Current) return;
        await ApplyAsync(store, prefs);
        Changed?.Invoke();
    }

    private PlaybackPreferences Current => new(CrossfadeSeconds, InfinitePlay, InfiniteAhead);

    private async Task ApplyAsync(ScopedStore store, PlaybackPreferences prefs)
    {
        CrossfadeSeconds = prefs.CrossfadeSeconds;
        InfinitePlay = prefs.InfinitePlay;
        InfiniteAhead = prefs.InfiniteAhead;
        await store.SetAsync(CrossfadeKey, CrossfadeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await store.SetAsync(InfiniteKey, InfinitePlay ? "1" : "0");
        await store.SetAsync(InfiniteAheadKey, InfiniteAhead.ToString());
    }

    public async Task SaveAsync(StreamQuality quality, StreamQuality mobileQuality, int prefetch, double crossfadeSeconds, bool infinitePlay, int infiniteAhead)
    {
        Quality = quality;
        MobileQuality = mobileQuality;
        PrefetchCount = Math.Clamp(prefetch, 0, 10);
        var store = new ScopedStore(_store, _accounts);
        await store.SetAsync(QualityKey, quality.ToWire());
        await store.SetAsync(MobileQualityKey, mobileQuality.ToWire());
        await store.SetAsync(PrefetchKey, PrefetchCount.ToString());
        var prefs = new PlaybackPreferences(crossfadeSeconds, infinitePlay, infiniteAhead).Clamped();
        await ApplyAsync(store, prefs);
        Changed?.Invoke();
        try
        {
            await _api.SavePlaybackPreferencesAsync(prefs);
            await store.SetAsync(PendingKey, null);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            await store.SetAsync(PendingKey, "1");
        }
    }

    private static double ClampFade(double seconds) => new PlaybackPreferences(seconds, false, 1).Clamped().CrossfadeSeconds;
}
