using System.Text;
using System.Text.RegularExpressions;
using SonNuage.Server.Radio;

namespace SonNuage.Server.Search;

/// <summary>
/// Ambiance (humeur, énergie, tempo, activité) : sa note d'après l'analyse audio, et les tags MusicBrainz ou genres qui la
/// signalent pour un morceau sans analyse (en minuscules, sans accents). <see cref="Label"/> : ses mots en français et
/// en anglais, pour décrire un morceau au modèle d'inférence de l'option sémantique.
/// </summary>
public sealed record AmbianceMood(string Id, string Label, Func<AudioFeel, double> Feel, IReadOnlyList<string> Tags, IReadOnlyList<string> Genres)
{
    /// <summary>Note d'un morceau sans analyse mais dont un tag ou le genre signale l'ambiance.</summary>
    public const double Signaled = 0.75;

    public double Score(TrackProfile p) => Math.Max(p.Feel is { } f ? Feel(f) : 0, IsSignaled(p) ? Signaled : 0);

    private bool IsSignaled(TrackProfile p) =>
        Tags.Any(p.Tags.Contains) || Genres.Any(g => AmbianceGenre.HasWords(p.Genre, g));
}

/// <summary>Genre demandé : <see cref="Name"/> tel que reconnu, <see cref="Fragments"/> cherchés dans le genre et les tags.</summary>
public sealed record AmbianceGenre(string Name, IReadOnlyList<string> Fragments)
{
    public bool Matches(TrackProfile p) =>
        Fragments.Any(f => HasWords(p.Genre, f) || p.Tags.Any(t => HasWords(t.Replace('-', ' '), f)));

    /// <summary>Vrai si <paramref name="phrase"/> apparaît en mots entiers dans <paramref name="text"/>.</summary>
    internal static bool HasWords(string text, string phrase) =>
        text.Length >= phrase.Length && (text == phrase || $" {text} ".Contains($" {phrase} ", StringComparison.Ordinal));
}

/// <summary>Années comprises (bornes incluses).</summary>
public sealed record YearRange(int From, int To);

/// <summary>
/// Demande d'ambiance lue par <see cref="AmbianceParser"/>. Genre et décennie sont exigés ; les ambiances donnent la note
/// (moyenne), retenue à partir de <see cref="Threshold"/>.
/// </summary>
/// <param name="Terms">Expressions reconnues, telles que saisies (en minuscules).</param>
/// <param name="Key">La demande en minuscules, sans accents ni ponctuation : identifiant de la radio d'ambiance.</param>
public sealed record AmbianceQuery(
    IReadOnlyList<AmbianceMood> Moods, IReadOnlyList<AmbianceGenre> Genres, YearRange? Years, IReadOnlyList<string> Terms, string Key)
{
    public const double Threshold = 0.55;

    public double Score(TrackProfile p)
    {
        if (Years is { } y && (p.Year is not { } year || year < y.From || year > y.To)) return 0;
        if (Genres.Count > 0 && !Genres.Any(g => g.Matches(p))) return 0;
        return Moods.Count == 0 ? 1 : Moods.Average(m => m.Score(p));
    }
}

/// <summary>
/// Lit une demande en langage courant (« musique calme pour travailler », « rock énergique années 90 », « sad songs »).
/// Tous les mots doivent être compris (vocabulaire, décennie, genre de la bibliothèque ou mot de liaison) et au moins
/// une ambiance ou une décennie demandée : sinon ce n'est pas une ambiance (un nom d'artiste, un titre…) et la
/// recherche ordinaire suffit.
/// </summary>
public static partial class AmbianceParser
{
    /// <summary>Expressions les plus longues essayées en premier (« love songs » avant « love »).</summary>
    private const int MaxPhraseWords = 3;

    private static readonly Dictionary<string, AmbianceMood[]> Moods = BuildMoods();
    private static readonly HashSet<string> Stopwords = AmbianceVocabulary.Stopwords.Select(TrackProfile.Fold).ToHashSet();
    private static readonly Dictionary<string, string[]> Aliases =
        AmbianceVocabulary.GenreAliases.ToDictionary(kv => TrackProfile.Fold(kv.Key), kv => kv.Value);

    /// <param name="libraryGenres">Genres présents dans la bibliothèque, tels qu'écrits dans les fichiers (« Hip-Hop », « Jazz; Blues »).</param>
    public static AmbianceQuery? Parse(string query, IEnumerable<string> libraryGenres)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return null;
        var genres = LibraryGenres(libraryGenres);

        var moods = new List<AmbianceMood>();
        var genreHits = new List<AmbianceGenre>();
        var terms = new List<string>();
        YearRange? years = null;
        for (var i = 0; i < tokens.Count;)
        {
            if (Match(tokens, i, genres) is not { } m) return null;
            if (m.Kind != Kind.Stopword) terms.Add(string.Join(' ', tokens.Skip(i).Take(m.Length).Select(t => t.Shown)));
            switch (m.Kind)
            {
                case Kind.Mood:
                    moods.AddRange(m.Moods!.Where(x => !moods.Contains(x)));
                    break;
                case Kind.Decade:
                    years = m.Years;
                    break;
                case Kind.Genre when genreHits.All(g => g.Name != m.Genre!.Name):
                    genreHits.Add(m.Genre!);
                    break;
            }
            i += m.Length;
        }
        if (moods.Count == 0 && years is null) return null;
        return new AmbianceQuery(moods, genreHits, years, terms, string.Join(' ', tokens.Select(t => t.Folded)));
    }

    private enum Kind { Mood, Decade, Genre, Stopword }

    private sealed record Hit(Kind Kind, int Length, AmbianceMood[]? Moods = null, YearRange? Years = null, AmbianceGenre? Genre = null);

    /// <summary>Mot saisi : <see cref="Shown"/> en minuscules avec ses accents, <see cref="Folded"/> sans (élision retirée).</summary>
    private sealed record Token(string Shown, string Folded);

    /// <summary>À longueur égale : ambiance, puis décennie, mot de liaison et enfin genre (« musique » n'est pas un genre).</summary>
    private static Hit? Match(List<Token> tokens, int start, LibraryGenreSet genres)
    {
        for (var n = Math.Min(MaxPhraseWords, tokens.Count - start); n >= 1; n--)
        {
            var phrase = string.Join(' ', tokens.Skip(start).Take(n).Select(t => t.Folded));
            if (Moods.TryGetValue(phrase, out var moods)) return new Hit(Kind.Mood, n, moods);
            if (Decade(tokens, start, n) is { } years) return new Hit(Kind.Decade, n, Years: years);
            if (n == 1 && Stopwords.Contains(phrase)) return new Hit(Kind.Stopword, 1);
            if (Genre(phrase, genres) is { } genre) return new Hit(Kind.Genre, n, Genre: genre);
        }
        return null;
    }

    private static AmbianceGenre? Genre(string phrase, LibraryGenreSet genres)
    {
        var alias = Aliases.GetValueOrDefault(phrase);
        if (alias is null && !genres.Recognizes(phrase)) return null;
        return new AmbianceGenre(phrase, (alias ?? []).Prepend(phrase).Distinct().ToList());
    }

    /// <summary>« 90s », « 90's », « 1990s », « années 90 », « années 2000 », « eighties ».</summary>
    private static YearRange? Decade(List<Token> tokens, int start, int n)
    {
        var first = tokens[start].Folded;
        string? digits = null;
        if (n == 1 && AmbianceVocabulary.DecadeWords.TryGetValue(first, out var from)) return new YearRange(from, from + 9);
        if (n == 1 && DecadeSuffix().Match(first) is { Success: true } m) digits = m.Groups[1].Value;
        if (n == 2 && first is "annees" or "annee" && DecadeDigits().IsMatch(tokens[start + 1].Folded)) digits = tokens[start + 1].Folded;
        if (digits is null) return null;
        var value = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (value % 10 != 0) return null;
        var year = digits.Length == 4 ? value : value >= 30 ? 1900 + value : 2000 + value;
        return year is >= 1900 and <= 2090 ? new YearRange(year, year + 9) : null;
    }

    [GeneratedRegex(@"^(\d{2}|\d{4})'?s$")]
    private static partial Regex DecadeSuffix();

    [GeneratedRegex(@"^(\d{2}|\d{4})$")]
    private static partial Regex DecadeDigits();

    [GeneratedRegex(@"^(?:l|d|j|qu|n|s|c|m|t)'(.+)$")]
    private static partial Regex Elision();

    /// <summary>Mots séparés par tout ce qui n'est ni lettre, ni chiffre, ni apostrophe ; élisions retirées (« d'amour » → « amour »).</summary>
    private static List<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        var word = new StringBuilder();
        foreach (var c in query.Normalize(NormalizationForm.FormC).Append(' '))
        {
            if (char.IsLetterOrDigit(c) || c is '\'' or '’' or '&') word.Append(c);
            else if (word.Length > 0)
            {
                Add(word.ToString());
                word.Clear();
            }
        }
        return tokens;

        void Add(string raw)
        {
            var shown = raw.Replace('’', '\'').Trim('\'').ToLowerInvariant();
            var folded = TrackProfile.Fold(shown);
            if (folded.Length == 0) return;
            if (Elision().Match(folded) is { Success: true } e)
            {
                folded = e.Groups[1].Value;
                shown = shown[(shown.IndexOf('\'') + 1)..];
            }
            tokens.Add(new Token(shown, folded));
        }
    }

    private static Dictionary<string, AmbianceMood[]> BuildMoods()
    {
        var map = new Dictionary<string, AmbianceMood[]>();
        foreach (var line in AmbianceVocabulary.Words)
        {
            foreach (var form in line.Forms.Select(TrackProfile.Fold))
                map[form] = map.TryGetValue(form, out var known) ? known.Union(line.Moods).ToArray() : line.Moods.ToArray();
        }
        return map;
    }

    private static LibraryGenreSet LibraryGenres(IEnumerable<string> genres) => new(genres
        .SelectMany(g => g.Split([';', ',', '/', '|'], StringSplitOptions.RemoveEmptyEntries))
        .Select(TrackProfile.Fold)
        .Where(g => g.Length > 0)
        .ToHashSet());

    /// <summary>Genres de la bibliothèque : reconnus en entier (« hip hop ») ou par leur dernier mot (« hard rock » → « rock »).</summary>
    private sealed record LibraryGenreSet(HashSet<string> Pieces)
    {
        public bool Recognizes(string phrase) => Pieces.Contains(phrase) || Pieces.Any(p => p.EndsWith(" " + phrase, StringComparison.Ordinal));
    }
}
