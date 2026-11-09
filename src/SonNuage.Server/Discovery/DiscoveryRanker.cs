using SonNuage.Server.Library;

namespace SonNuage.Server.Discovery;

/// <summary>Un artiste de la bibliothèque, son poids (les plus écoutés pèsent plus) et ses artistes proches.</summary>
public sealed record DiscoverySeed(string Name, double Weight, IReadOnlyList<SimilarArtist> Similar);

/// <summary>Artiste à découvrir : proximité cumulée et artistes de la bibliothèque qui y mènent, du plus proche au moins proche.</summary>
public sealed record RankedArtist(string MusicBrainzId, string Name, string? Comment, double Score, IReadOnlyList<string> BecauseOf);

public static class DiscoveryRanker
{
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
