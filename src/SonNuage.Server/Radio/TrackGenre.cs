using SonNuage.Server.Analysis;

namespace SonNuage.Server.Radio;

/// <summary>
/// Genre d'un morceau pour les radios, dans le vocabulaire Discogs : grands genres (<see cref="Genres"/>, « rock »,
/// « funk soul »…) et styles (<see cref="Styles"/>, « pop rock », « hard rock »…), en minuscules sans accents ni
/// ponctuation (<see cref="TrackProfile.Fold"/>). Source : les styles trouvés par l'analyse audio, pareils pour toute la
/// bibliothèque ; à défaut, le genre du fichier et les tags MusicBrainz, lus mot à mot.
/// </summary>
public sealed record TrackGenre(IReadOnlySet<string> Genres, IReadOnlySet<string> Styles)
{
    public static readonly TrackGenre Unknown = new(new HashSet<string>(), new HashSet<string>());

    /// <summary>Activation minimale d'un style gardé, et part de celle du style le plus probable.</summary>
    private const double MinScore = 0.1, ShareOfBest = 0.5;

    public bool Known => Genres.Count > 0;

    public static TrackGenre From(string? analysisStyles, string? fileGenre, IEnumerable<string>? tags)
    {
        var styles = DiscogsStyles.Decode(analysisStyles);
        return styles.Count > 0 ? FromAnalysis(styles) : FromWords(fileGenre, tags);
    }

    /// <summary>Le style le plus probable, et ceux qui en approchent.</summary>
    private static TrackGenre FromAnalysis(IReadOnlyList<DiscogsStyles.Style> styles)
    {
        var best = styles[0].Score;
        var kept = styles.Where((s, i) => i == 0 || s.Score >= Math.Max(MinScore, ShareOfBest * best)).ToList();
        return new TrackGenre(kept.Select(s => TrackProfile.Fold(s.Genre)).ToHashSet(), kept.Select(s => TrackProfile.Fold(s.Name)).ToHashSet());
    }

    private static TrackGenre FromWords(string? fileGenre, IEnumerable<string>? tags)
    {
        var genres = new HashSet<string>();
        var styles = new HashSet<string>();
        var words = (fileGenre ?? "").Split([';', '/', ',']).Concat((tags ?? []).SelectMany(t => t.Split('/')));
        foreach (var word in words.Select(TrackProfile.Fold).Where(w => w.Length > 0))
        {
            if (Taxonomy.Genres.Contains(word)) genres.Add(word);
            else if (Aliases.TryGetValue(word, out var alias))
            {
                genres.Add(alias.Genre);
                if (alias.Style is not null) styles.Add(alias.Style);
            }
            else if (Taxonomy.StyleGenres.TryGetValue(word, out var parents))
            {
                styles.Add(word);
                genres.UnionWith(parents);
            }
        }
        return genres.Count == 0 ? Unknown : new TrackGenre(genres, styles);
    }

    /// <summary>Mots courants des genres de fichiers et des tags qui ne sont pas des noms Discogs.</summary>
    private static readonly Dictionary<string, (string Genre, string? Style)> Aliases = new()
    {
        ["metal"] = ("rock", "heavy metal"),
        ["alternative"] = ("rock", "alternative rock"),
        ["indie"] = ("rock", "indie rock"),
        ["rock and roll"] = ("rock", "rock roll"),
        ["rock n roll"] = ("rock", "rock roll"),
        ["rap"] = ("hip hop", null),
        ["r b"] = ("funk soul", "contemporary r b"),
        ["rnb"] = ("funk soul", "contemporary r b"),
        ["rhythm and blues"] = ("funk soul", "rhythm blues"),
        ["dance"] = ("electronic", null),
        ["edm"] = ("electronic", null),
        ["electropop"] = ("electronic", "synth pop"),
        ["world"] = ("folk world country", null),
        ["variete"] = ("pop", null),
        ["variete francaise"] = ("pop", "chanson"),
        ["chanson francaise"] = ("pop", "chanson"),
    };

    private static class Taxonomy
    {
        public static readonly HashSet<string> Genres = [];
        public static readonly Dictionary<string, HashSet<string>> StyleGenres = [];

        static Taxonomy()
        {
            foreach (var label in DiscogsTaxonomy.Labels)
            {
                var parts = label.Split("---");
                var genre = TrackProfile.Fold(parts[0]);
                Genres.Add(genre);
                var style = TrackProfile.Fold(parts[1]);
                if (!StyleGenres.TryGetValue(style, out var set)) StyleGenres[style] = set = [];
                set.Add(genre);
            }
        }
    }
}
