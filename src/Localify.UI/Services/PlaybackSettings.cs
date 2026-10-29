using Localify.Contracts;

namespace Localify.UI.Services;

/// <summary>Réglages de lecture, propres au compte actif (rechargés à chaque changement de compte).</summary>
public sealed class PlaybackSettings
{
    private const string QualityKey = "localify.quality";
    private const string MobileQualityKey = "localify.quality.mobile";
    private const string PrefetchKey = "localify.prefetch";

    public StreamQuality Quality { get; private set; } = StreamQuality.Normal;

    /// <summary>Qualité sur réseau mobile (Android). Plus basse par défaut.</summary>
    public StreamQuality MobileQuality { get; private set; } = StreamQuality.Low;

    /// <summary>Nombre de pistes suivantes téléchargées d'avance.</summary>
    public int PrefetchCount { get; private set; } = 2;

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
        if (await store.GetAsync(QualityKey) is { } rawQ && StreamQualities.TryParse(rawQ, out var q)) Quality = q;
        if (await store.GetAsync(MobileQualityKey) is { } rawM && StreamQualities.TryParse(rawM, out var m)) MobileQuality = m;
        if (int.TryParse(await store.GetAsync(PrefetchKey), out var p)) PrefetchCount = Math.Clamp(p, 0, 10);
        Changed?.Invoke();
    }

    public async Task SaveAsync(StreamQuality quality, StreamQuality mobileQuality, int prefetch)
    {
        Quality = quality;
        MobileQuality = mobileQuality;
        PrefetchCount = Math.Clamp(prefetch, 0, 10);
        var store = new ScopedStore(_store, _accounts);
        await store.SetAsync(QualityKey, quality.ToWire());
        await store.SetAsync(MobileQualityKey, mobileQuality.ToWire());
        await store.SetAsync(PrefetchKey, PrefetchCount.ToString());
        Changed?.Invoke();
    }
}
