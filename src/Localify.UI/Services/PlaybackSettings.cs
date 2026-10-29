using Localify.Contracts;

namespace Localify.UI.Services;

public sealed class PlaybackSettings(IKeyValueStore store)
{
    private const string QualityKey = "localify.quality";
    private const string MobileQualityKey = "localify.quality.mobile";
    private const string PrefetchKey = "localify.prefetch";

    public StreamQuality Quality { get; private set; } = StreamQuality.Normal;

    /// <summary>Qualité sur réseau mobile (Android). Plus basse par défaut.</summary>
    public StreamQuality MobileQuality { get; private set; } = StreamQuality.Low;

    /// <summary>Nombre de pistes suivantes téléchargées d'avance.</summary>
    public int PrefetchCount { get; private set; } = 2;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        if (await store.GetAsync(QualityKey) is { } rawQ && StreamQualities.TryParse(rawQ, out var q)) Quality = q;
        if (await store.GetAsync(MobileQualityKey) is { } rawM && StreamQualities.TryParse(rawM, out var m)) MobileQuality = m;
        if (int.TryParse(await store.GetAsync(PrefetchKey), out var p)) PrefetchCount = Math.Clamp(p, 0, 10);
    }

    public async Task SaveAsync(StreamQuality quality, StreamQuality mobileQuality, int prefetch)
    {
        Quality = quality;
        MobileQuality = mobileQuality;
        PrefetchCount = Math.Clamp(prefetch, 0, 10);
        await store.SetAsync(QualityKey, quality.ToWire());
        await store.SetAsync(MobileQualityKey, mobileQuality.ToWire());
        await store.SetAsync(PrefetchKey, PrefetchCount.ToString());
        Changed?.Invoke();
    }
}
