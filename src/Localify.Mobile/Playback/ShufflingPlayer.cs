using AndroidX.Media3.Common;

namespace Localify.Mobile.Playback;

/// <summary>
/// Aléatoire « physique » : activer le shuffle réordonne réellement la file (piste en cours en tête, suite mélangée)
/// et le désactiver restaure l'ordre d'origine. Ainsi la file vue par l'appli, la notification et Android Auto
/// est toujours l'ordre de lecture réel, et le pré-chargement sait quelles pistes viennent ensuite.
/// L'ExoPlayer sous-jacent utilise un ordre de mélange neutre (UnshuffledShuffleOrder).
/// </summary>
public sealed class ShufflingPlayer(Media3IPlayer player) : ForwardingPlayer(player)
{
    private List<MediaItem>? _original;

    public override bool ShuffleModeEnabled
    {
        get => base.ShuffleModeEnabled;
        set
        {
            if (value == base.ShuffleModeEnabled) return;
            if (value) Shuffle();
            else Unshuffle();
            base.ShuffleModeEnabled = value;
        }
    }

    public override void SetMediaItems(IList<MediaItem>? mediaItems) => SetMediaItems(mediaItems, 0, 0);

    public override void SetMediaItems(IList<MediaItem>? mediaItems, bool resetPosition)
    {
        if (resetPosition || !base.ShuffleModeEnabled) SetMediaItems(mediaItems, 0, 0);
        else base.SetMediaItems(mediaItems, false);
    }

    public override void SetMediaItems(IList<MediaItem>? mediaItems, int startIndex, long startPositionMs)
    {
        if (!base.ShuffleModeEnabled || mediaItems is null || mediaItems.Count < 2)
        {
            _original = null;
            base.SetMediaItems(mediaItems, startIndex, startPositionMs);
            return;
        }
        var start = Math.Clamp(startIndex, 0, mediaItems.Count - 1);
        _original = mediaItems.ToList();
        var rest = mediaItems.Where((_, i) => i != start).ToArray();
        Random.Shared.Shuffle(rest);
        base.SetMediaItems([mediaItems[start], .. rest], 0, startPositionMs);
    }

    private void Shuffle()
    {
        var items = this.Items();
        if (items.Count < 2) return;
        var current = Math.Max(0, CurrentMediaItemIndex);
        _original = items;
        var rest = items.Where((_, i) => i != current).ToArray();
        Random.Shared.Shuffle(rest);
        ReplaceAround(current, [], rest);
    }

    private void Unshuffle()
    {
        if (_original is null) return;
        var items = this.Items();
        var current = Math.Max(0, CurrentMediaItemIndex);
        if (items.Count < 2 || current >= items.Count) return;

        // L'ordre d'origine, limité aux éléments encore présents (multi-ensemble : une piste peut figurer deux fois),
        // suivi de ce qui a été ajouté pendant le shuffle.
        var remaining = items.ToList();
        var order = new List<MediaItem>(items.Count);
        foreach (var item in _original)
        {
            var j = remaining.FindIndex(r => r.Equals(item));
            if (j < 0) continue;
            order.Add(remaining[j]);
            remaining.RemoveAt(j);
        }
        order.AddRange(remaining);
        _original = null;

        var playing = items[current];
        var k = order.FindIndex(o => o.Equals(playing));
        if (k < 0) return;
        ReplaceAround(current, order[..k], order[(k + 1)..]);
    }

    /// <summary>Remplace tout sauf la piste en cours, sans interrompre la lecture.</summary>
    private void ReplaceAround(int current, List<MediaItem> before, IList<MediaItem> after)
    {
        var count = MediaItemCount;
        if (current + 1 < count) base.RemoveMediaItems(current + 1, count);
        if (current > 0) base.RemoveMediaItems(0, current);
        if (before.Count > 0) base.AddMediaItems(0, before);
        if (after.Count > 0) base.AddMediaItems(before.Count + 1, after);
    }
}
