using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Lecture infinie : quand la file s'épuise, des morceaux proches s'ajoutent à la fin pour qu'il en reste toujours
/// « avance » après celui qui joue. Avec 5 morceaux et une avance de 3, le 6e arrive quand le 3e commence.
/// </summary>
public static class InfinitePlay
{
    public const int DefaultAhead = PlaybackPreferences.DefaultAhead;
    public const int MaxAhead = PlaybackPreferences.MaxAhead;
    /// <summary>Morceaux de la file qui servent de point de départ (les derniers écoutés).</summary>
    public const int SeedCount = 4;

    /// <summary>Nombre de morceaux à ajouter maintenant (0 si la file en a assez ou si rien ne joue).</summary>
    public static int Needed(int count, int currentIndex, int ahead) =>
        currentIndex < 0 || currentIndex >= count ? 0 : Math.Max(0, ahead - (count - 1 - currentIndex));

    /// <summary>Les derniers morceaux joués, jusqu'à celui en cours compris.</summary>
    public static IReadOnlyList<Guid> Seeds(IReadOnlyList<Guid> queue, int currentIndex) =>
        queue.Take(currentIndex + 1).TakeLast(SeedCount).ToList();
}
