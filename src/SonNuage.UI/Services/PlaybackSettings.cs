using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Réglages de lecture, propres au compte actif (rechargés à chaque changement de compte).</summary>
public sealed class PlaybackSettings
{
    private const string QualityKey = "sonnuage.quality";
    private const string MobileQualityKey = "sonnuage.quality.mobile";
    private const string PrefetchKey = "sonnuage.prefetch";
    private const string CrossfadeKey = "sonnuage.crossfade";
    private const string InfiniteKey = "sonnuage.infinite";
    private const string InfiniteAheadKey = "sonnuage.infinite.ahead";
    public const double MaxCrossfadeSeconds = 5;

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

    public PlaybackSettings(IKeyValueStore store, AccountStore accounts)
    {
        _store = store;
        _accounts = accounts;
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
    }

    public async Task SaveAsync(StreamQuality quality, StreamQuality mobileQuality, int prefetch, double crossfadeSeconds, bool infinitePlay, int infiniteAhead)
    {
        Quality = quality;
        MobileQuality = mobileQuality;
        PrefetchCount = Math.Clamp(prefetch, 0, 10);
        CrossfadeSeconds = ClampFade(crossfadeSeconds);
        InfinitePlay = infinitePlay;
        InfiniteAhead = Math.Clamp(infiniteAhead, 1, Services.InfinitePlay.MaxAhead);
        var store = new ScopedStore(_store, _accounts);
        await store.SetAsync(QualityKey, quality.ToWire());
        await store.SetAsync(MobileQualityKey, mobileQuality.ToWire());
        await store.SetAsync(PrefetchKey, PrefetchCount.ToString());
        await store.SetAsync(CrossfadeKey, CrossfadeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await store.SetAsync(InfiniteKey, InfinitePlay ? "1" : "0");
        await store.SetAsync(InfiniteAheadKey, InfiniteAhead.ToString());
        Changed?.Invoke();
    }

    private static double ClampFade(double seconds) => Math.Round(Math.Clamp(seconds, 0, MaxCrossfadeSeconds), 1);
}
