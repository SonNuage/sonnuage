using SonNuage.Server.Library;

namespace SonNuage.Server.Discovery;

/// <summary>Un artiste de la bibliothèque, son poids (les plus écoutés pèsent plus) et ses artistes proches.</summary>
public sealed record DiscoverySeed(string Name, double Weight, IReadOnlyList<SimilarArtist> Similar);

/// <summary>Artiste à découvrir : proximité cumulée et artistes de la bibliothèque qui y mènent, du plus proche au moins proche.</summary>
public sealed record RankedArtist(string MusicBrainzId, string Name, string? Comment, double Score, IReadOnlyList<string> BecauseOf);

/// <summary>Ce que change une actualisation : artistes retirés (trop anciens) et nouveaux artistes ajoutés.</summary>
public sealed record DiscoveryUpdate(IReadOnlyList<string> Expired, IReadOnlyList<RankedArtist> Added);

public static class DiscoveryRanker
{
    /// <summary>Une proposition reste affichée 7 jours, puis part (sans être écartée pour la suite).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Actualisation incrémentale : les propositions de plus de 7 jours partent ; les meilleurs artistes pas encore
    /// affichés s'ajoutent, <paramref name="newPerRun"/> à la fois, ou plus pour revenir à <paramref name="minShown"/>.
    /// Un artiste qui part ne revient pas dans la même actualisation, mais peut revenir dans une suivante.
    /// </summary>
    public static DiscoveryUpdate Plan(IReadOnlyList<RankedArtist> ranked, IReadOnlyCollection<(string MusicBrainzId, DateTimeOffset AddedAt)> current,
        DateTimeOffset now, int newPerRun, int minShown, TimeSpan? maxAge = null)
    {
        var age = maxAge ?? MaxAge;
        var expired = current.Where(c => now - c.AddedAt >= age).Select(c => c.MusicBrainzId).ToList();
        var shown = current.Select(c => c.MusicBrainzId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = Math.Max(newPerRun, minShown - (current.Count - expired.Count));
        return new DiscoveryUpdate(expired, ranked.Where(a => !shown.Contains(a.MusicBrainzId)).Take(wanted).ToList());
    }

    /// <summary>
    /// Cumule, pour chaque artiste proposé, sa proximité avec chaque artiste de la bibliothèque (rapportée au plus
    /// proche de cet artiste, puis multipliée par son poids). Les artistes déjà dans la bibliothèque (même identifiant
    /// MusicBrainz ou même nom) sont écartés.
    /// </summary>
    public static IReadOnlyList<RankedArtist> Rank(IReadOnlyList<DiscoverySeed> seeds, IEnumerable<string> libraryMbids,
        IEnumerable<string> libraryNames, int max)
    {
        var mbids = libraryMbids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = libraryNames.Select(NameNormalizer.Key).ToHashSet();
        var found = new Dictionary<string, (SimilarArtist Artist, double Score, List<(string Seed, double Part)> Seeds)>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            var best = seed.Similar.Count == 0 ? 1 : Math.Max(seed.Similar.Max(s => s.Score), 1);
            foreach (var similar in seed.Similar)
            {
                if (mbids.Contains(similar.MusicBrainzId) || names.Contains(NameNormalizer.Key(similar.Name))) continue;
                var part = seed.Weight * similar.Score / best;
                if (!found.TryGetValue(similar.MusicBrainzId, out var entry)) entry = (similar, 0, []);
                entry.Seeds.Add((seed.Name, part));
                found[similar.MusicBrainzId] = (entry.Artist, entry.Score + part, entry.Seeds);
            }
        }

        return found.Values
            .OrderByDescending(f => f.Score)
            .Take(max)
            .Select(f => new RankedArtist(f.Artist.MusicBrainzId, f.Artist.Name, f.Artist.Comment, f.Score,
                f.Seeds.OrderByDescending(s => s.Part).Select(s => s.Seed).Distinct().ToList()))
            .ToList();
    }
}
