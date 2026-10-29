using System.Text.Json;
using AndroidX.Media3.Common;
using Localify.UI.Services;

namespace Localify.Mobile.Playback;

/// <summary>Sauvegarde de la file pour la reprise (bouton lecture d'Android Auto, redémarrage de l'appli).</summary>
public sealed class QueueStore(IKeyValueStore store, ApiClient api, LibraryBrowser browser)
{
    private const string Key = "localify.queue";

    public sealed record Saved(List<Guid> Ids, int Index, long PositionMs, int RepeatMode);

    /// <summary>À appeler sur le thread du lecteur ; l'écriture se fait ensuite en arrière-plan.</summary>
    public void Save(Media3IPlayer player)
    {
        var ids = player.Items().Take(500).Select(i => TrackItems.ToTrack(i)?.Id).OfType<Guid>().ToList();
        var saved = new Saved(ids, Math.Max(0, player.CurrentMediaItemIndex), Math.Max(0, player.CurrentPosition), player.RepeatMode);
        _ = store.SetAsync(Key, JsonSerializer.Serialize(saved, JsonDefaults.Options));
    }

    public async Task<(List<MediaItem> Items, int Index, long PositionMs, int RepeatMode)?> LoadAsync()
    {
        if (!await browser.EnsureSignedInAsync()) return null;
        var raw = await store.GetAsync(Key);
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
