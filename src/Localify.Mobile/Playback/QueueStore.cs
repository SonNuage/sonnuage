using System.Text.Json;
using AndroidX.Media3.Common;
using Localify.UI.Services;

namespace Localify.Mobile.Playback;

/// <summary>
/// Sauvegarde de la file pour la reprise (bouton lecture d'Android Auto, redémarrage de l'appli).
/// Une file par compte : la clé est suffixée par l'identifiant du compte (<see cref="AccountStore.ScopedKey"/>).
/// </summary>
public sealed class QueueStore(IKeyValueStore store, AccountStore accounts, ApiClient api, LibraryBrowser browser)
{
    private const string BaseKey = "localify.queue";

    public sealed record Saved(List<Guid> Ids, int Index, long PositionMs, int RepeatMode);

    /// <summary>Clé de la file du compte actif.</summary>
    public string CurrentKey => accounts.ScopedKey(BaseKey);

    /// <summary>
    /// À appeler sur le thread du lecteur ; l'écriture se fait ensuite en arrière-plan. <paramref name="key"/> est celle
    /// du compte à qui appartient la file jouée (null : rien à sauver, par exemple pendant un changement de compte).
    /// </summary>
    public void Save(Media3IPlayer player, string? key)
    {
        if (key is null) return;
        var ids = player.Items().Take(500).Select(i => TrackItems.ToTrack(i)?.Id).OfType<Guid>().ToList();
        var saved = new Saved(ids, Math.Max(0, player.CurrentMediaItemIndex), Math.Max(0, player.CurrentPosition), player.RepeatMode);
        _ = store.SetAsync(key, JsonSerializer.Serialize(saved, JsonDefaults.Options));
    }

    /// <summary>File sauvegardée du compte actif, pistes rechargées depuis son serveur.</summary>
    public async Task<(List<MediaItem> Items, int Index, long PositionMs, int RepeatMode)?> LoadAsync()
    {
        if (!await browser.EnsureSignedInAsync()) return null;
        var raw = await store.GetAsync(CurrentKey);
        if (raw is null) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(raw, JsonDefaults.Options);
            if (saved is null || saved.Ids.Count == 0) return null;
            var current = saved.Ids[Math.Clamp(saved.Index, 0, saved.Ids.Count - 1)];
            var tracks = await api.LookupTracksAsync(saved.Ids) ?? [];
            if (tracks.Count == 0) return null;
            var index = Math.Max(0, tracks.FindIndex(t => t.Id == current));
            return (tracks.Select(browser.Item).ToList(), index, tracks[index].Id == current ? saved.PositionMs : 0, saved.RepeatMode);
        }
        catch (Exception ex) when (ex is JsonException or ApiException or HttpRequestException)
        {
            return null;
        }
    }
}
