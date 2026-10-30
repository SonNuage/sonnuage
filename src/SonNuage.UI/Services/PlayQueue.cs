using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// File de lecture avec shuffle et repeat. L'ordre mélangé est figé (pas de tirage à chaque piste) :
/// « précédent » revient bien sur la piste précédente et les pistes suivantes sont connues pour le pré-chargement.
/// </summary>
public sealed class PlayQueue
{
    private List<TrackDto> _items = [];
    private readonly Random _random = new();

    public IReadOnlyList<TrackDto> Items => _items;
    public int Index { get; private set; } = -1;
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; set; }

    /// <summary>Ordre d'origine, pour pouvoir désactiver le shuffle.</summary>
    private List<TrackDto> _original = [];

    public TrackDto? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;

    public void Load(IReadOnlyList<TrackDto> tracks, int startIndex, bool shuffle)
    {
        _original = tracks.ToList();
        _items = tracks.ToList();
        Index = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
        Shuffle = false;
        if (shuffle) SetShuffle(true, keepCurrentFirst: startIndex >= 0);
    }

    /// <param name="keepCurrentFirst">La piste en cours devient la première de l'ordre mélangé.</param>
    public void SetShuffle(bool shuffle, bool keepCurrentFirst = true)
    {
        if (shuffle == Shuffle || _items.Count == 0)
        {
            Shuffle = shuffle;
            return;
        }
        var current = Current;
        Shuffle = shuffle;
        if (shuffle)
        {
            var rest = _items.Where((_, i) => !keepCurrentFirst || i != Index).ToList();
            ShuffleInPlace(rest);
            _items = keepCurrentFirst && current is not null ? [current, .. rest] : rest;
            Index = 0;
        }
        else
        {
            _items = _original.ToList();
            Index = current is null ? 0 : Math.Max(0, _items.FindIndex(t => t.Id == current.Id));
        }
    }

    /// <summary>Index suivant, ou -1 en fin de file. <paramref name="auto"/> = fin naturelle de piste (repeat one s'applique).</summary>
    public int PeekNext(bool auto)
    {
        if (_items.Count == 0) return -1;
        if (auto && Repeat == RepeatMode.One) return Index;
        if (Index + 1 < _items.Count) return Index + 1;
        return Repeat == RepeatMode.Off ? -1 : 0;
    }

    public int PeekPrevious()
    {
        if (_items.Count == 0) return -1;
        if (Index > 0) return Index - 1;
        return Repeat == RepeatMode.All ? _items.Count - 1 : 0;
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < _items.Count) Index = index;
    }

    /// <summary>Les <paramref name="count"/> pistes qui suivront, dans l'ordre, pour le pré-chargement.</summary>
    public IReadOnlyList<TrackDto> Upcoming(int count)
    {
        var result = new List<TrackDto>();
        if (_items.Count == 0 || count <= 0) return result;
        var i = Index;
        for (var n = 0; n < Math.Min(count, _items.Count - 1); n++)
        {
            i++;
            if (i >= _items.Count)
            {
                if (Repeat == RepeatMode.Off) break;
                i = 0;
            }
            result.Add(_items[i]);
        }
        return result;
    }

    public void Enqueue(IReadOnlyList<TrackDto> tracks, bool playNext)
    {
        if (_items.Count == 0)
        {
            Load(tracks, 0, false);
            return;
        }
        var at = playNext ? Index + 1 : _items.Count;
        _items.InsertRange(at, tracks);
        _original.AddRange(tracks);
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var removed = _items[index];
        _items.RemoveAt(index);
        var originalIndex = _original.FindIndex(t => t.Id == removed.Id);
        if (originalIndex >= 0) _original.RemoveAt(originalIndex);
        if (index < Index) Index--;
        if (Index >= _items.Count) Index = _items.Count - 1;
    }

    public void Replace(TrackDto track)
    {
        for (var i = 0; i < _items.Count; i++) if (_items[i].Id == track.Id) _items[i] = track;
        for (var i = 0; i < _original.Count; i++) if (_original[i].Id == track.Id) _original[i] = track;
    }

    private void ShuffleInPlace(List<TrackDto> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
