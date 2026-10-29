using AndroidX.Media3.DataSource;
using AndroidX.Media3.DataSource.Cache;
using Localify.Contracts;
using Localify.Mobile.Services;
using Localify.UI.Services;

namespace Localify.Mobile.Playback;

/// <summary>
/// Résout localify://track/{id} en URL de flux au moment du chargement : qualité selon le réseau (Wi-Fi / mobile)
/// et jeton média renouvelé. La clé de cache ne dépend que de la piste et de la qualité, pas du jeton.
/// Si une autre qualité est déjà entièrement en cache (pré-chargée en Wi-Fi par exemple), elle est préférée :
/// aucun octet réseau.
/// Le cache est cloisonné par compte : la clé commence par l'identifiant du compte actif.
/// </summary>
public sealed class StreamResolver(ApiClient api, PlaybackSettings settings, AccountStore accounts, ICache cache) : Java.Lang.Object, ResolvingDataSource.IResolver
{
    public DataSpec? ResolveDataSpec(DataSpec? dataSpec)
    {
        if (dataSpec is null || TrackItems.TrackIdFromUri(dataSpec.Uri) is not { } id) return dataSpec;

        var wanted = MobilePlatform.IsMetered() ? settings.MobileQuality : settings.Quality;
        var quality = new[] { wanted, settings.Quality, settings.MobileQuality }.FirstOrDefault(q => IsFullyCached(Key(id, q)), wanted);

        // Appelé sur le thread de chargement d'ExoPlayer : attente synchrone acceptable (pas de contexte UI).
        var url = api.StreamUrlAsync(id, quality).GetAwaiter().GetResult()
            ?? throw new Java.IO.IOException("Session expirée : reconnectez-vous dans l'application.");
        return dataSpec.BuildUpon()!
            .SetUri(Android.Net.Uri.Parse(url))!
            .SetKey(Key(id, quality))!
            .Build();
    }

    public string Key(Guid id, StreamQuality quality) => $"{accounts.Active?.Id:N}/{id:N}-{quality.ToWire()}";

    private bool IsFullyCached(string key)
    {
        var length = cache.GetContentMetadata(key)?.Get("exo_len", -1) ?? -1;
        return length > 0 && cache.IsCached(key, 0, length);
    }
}
