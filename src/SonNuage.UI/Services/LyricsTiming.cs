using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Paroles synchronisées : lignes triées par instant de départ (comme les envoie le serveur).</summary>
public static class LyricsTiming
{
    /// <summary>La dernière ligne commencée à cette position, -1 avant la première.</summary>
    public static int CurrentIndex(IReadOnlyList<LyricLineDto> lines, double position)
    {
        var current = -1;
        for (var i = 0; i < lines.Count && (lines[i].StartSeconds ?? double.MaxValue) <= position; i++) current = i;
        return current;
    }

    /// <summary>Temps restant avant la ligne suivante, null après la dernière.</summary>
    public static TimeSpan? UntilNext(IReadOnlyList<LyricLineDto> lines, double position) =>
        lines.FirstOrDefault(l => l.StartSeconds > position)?.StartSeconds is { } next
            ? TimeSpan.FromSeconds(next - position)
            : null;
}

/// <summary>
/// Position du lecteur entre deux rapports : le lecteur ne la donne qu'une fois par seconde, trop peu pour suivre
/// une ligne de paroles. On avance avec l'horloge tant que la lecture continue, deux secondes au plus sans nouvelles.
/// </summary>
public sealed class LyricsClock(TimeProvider time)
{
    private static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(2);
    private double _reported;
    private bool _running;
    private DateTimeOffset _at = time.GetUtcNow();

    public double Position => _running
        ? _reported + Math.Min((time.GetUtcNow() - _at).TotalSeconds, MaxAhead.TotalSeconds)
        : _reported;

    /// <summary>À chaque changement d'état du lecteur : une nouvelle position ou une pause repart de là.</summary>
    public void Observe(PlayerState state)
    {
        var running = state is { IsPlaying: true, IsBuffering: false };
        if (state.PositionSeconds == _reported && running == _running) return;
        _reported = state.PositionSeconds;
        _running = running;
        _at = time.GetUtcNow();
    }
}
