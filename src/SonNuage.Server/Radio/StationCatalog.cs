using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SonNuage.Contracts;

namespace SonNuage.Server.Radio;

/// <summary>Caractéristiques sonores utiles aux radios (probabilités de 0 à 1, tempo en BPM).</summary>
public sealed record AudioFeel(
    double? Bpm = null, double? Danceability = null, double? Energy = null, double? Valence = null, double? Happy = null,
    double? Sad = null, double? Relaxed = null, double? Aggressive = null, double? Party = null, double? Acoustic = null,
    double? Electronic = null, double? Instrumental = null)
{
    /// <summary>Bornes réelles de l'éveil et de la valence mesurés (emoMusic), ramenées à 0..1.</summary>
    private const double MeasuredLow = 0.35, MeasuredHigh = 0.75;

    /// <summary>
    /// Depuis l'analyse audio : l'éveil (<see cref="Energy"/>) et la valence d'emoMusic ne couvrent qu'environ 0,35 à
    /// 0,75 ; on les étire sur toute l'échelle pour qu'ils pèsent autant que les autres probabilités.
    /// </summary>
    public static AudioFeel FromAnalysis(
        double? Bpm = null, double? Danceability = null, double? Energy = null, double? Valence = null, double? Happy = null,
        double? Sad = null, double? Relaxed = null, double? Aggressive = null, double? Party = null, double? Acoustic = null,
        double? Electronic = null, double? Instrumental = null) =>
        new(Bpm, Danceability, Stretch(Energy), Stretch(Valence), Happy, Sad, Relaxed, Aggressive, Party, Acoustic, Electronic, Instrumental);

    private static double? Stretch(double? v) =>
        v is { } x ? Math.Clamp((x - MeasuredLow) / (MeasuredHigh - MeasuredLow), 0, 1) : null;
}

/// <summary>
/// Morceau vu par les radios. <see cref="Text"/> : titre et album en minuscules sans accents, entourés d'espaces ;
/// <see cref="Genre"/> : genre du fichier en minuscules sans accents ; <see cref="Tags"/> : tags MusicBrainz ;
/// <see cref="Year"/> : année de sortie, si connue ; <see cref="Song"/> : la chanson, commune à ses versions
/// (<see cref="SongOf"/>).
/// </summary>
public sealed record TrackProfile(
    Guid Id, Guid ArtistId, Guid AlbumId, bool AlbumHasCover, double DurationSeconds, string Text, string Genre,
    IReadOnlySet<string> Tags, AudioFeel? Feel, int? Year = null, string Song = "")
{
    public static TrackProfile Create(Guid id, Guid artistId, Guid albumId, bool albumHasCover, double durationSeconds, string title,
        string? albumTitle, string? genre, IEnumerable<string>? tags = null, AudioFeel? feel = null, int? year = null) =>
        new(id, artistId, albumId, albumHasCover, durationSeconds, $" {Fold(title)} {Fold(albumTitle)} ", Fold(genre),
            (tags ?? []).Select(t => t.ToLowerInvariant()).ToHashSet(), feel, year, SongOf(title));

    /// <summary>Mention entre parenthèses ou crochets, ou après un tiret en fin de titre.</summary>
    private static readonly Regex Mention = new(@"[\(\[][^\)\]]*[\)\]]|\s[-–—]\s[^-–—]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Mots (sans accents) qui font d'une mention celle d'une version : remix, live, remaster, invité, année…</summary>
    private static readonly Regex VersionWord = new(
        @"(?<![a-z\d])([a-z]*mix(ed)?|version|versione|versao|live|edit|remaster(ed)?|remasterise|acoustic|acoustique|acustic[ao]|demo|mono"
        + @"|stereo|instrumental|karaoke|radio|single|album|take|session|bonus|feat|ft|featuring|with|avec|con|duet|dueto|duo|original"
        + @"|extended|club|dub|deluxe|explicit|clean|anniversary|(19|20)\d\d)(?![a-z\d])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// La chanson d'un titre : le titre sans ses mentions de version (« (2019 stereo mix) », « - Radio Edit », « [live] »),
    /// en minuscules sans accents. Les autres mentions restent : « (Part II) » ou « (reprise) » sont d'autres morceaux.
    /// </summary>
    public static string SongOf(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var song = Fold(Mention.Replace(title, m => VersionWord.IsMatch(Fold(m.Value)) ? " " : m.Value));
        return song.Length > 0 ? song : Fold(title);
    }

    /// <summary>Minuscules, sans accents ; ponctuation remplacée par des espaces (l'apostrophe reste).</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c is '\'' or '’' ? '\'' : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Période de l'année (bornes comprises), à cheval sur deux années si elle finit avant de commencer.</summary>
public sealed record SeasonWindow(int FromMonth, int FromDay, int ToMonth, int ToDay)
{
    public bool Contains(DateOnly date)
    {
        var d = date.Month * 100 + date.Day;
        int from = FromMonth * 100 + FromDay, to = ToMonth * 100 + ToDay;
        return from <= to ? d >= from && d <= to : d >= from || d <= to;
    }
}

/// <summary>
/// Radio thématique : une note de 0 à 1 par morceau, retenu au-dessus de <see cref="Threshold"/>. Les clés de texte
/// sont <c>radio.stations.{Id}.name</c> et <c>.description</c>.
/// </summary>
public sealed record StationDefinition(string Id, string Kind, string Color, SeasonWindow? Window, Func<TrackProfile, double> Score,
    double Threshold = 0.6);

/// <summary>
/// Les radios fournies : saisons (visibles à leur période), ambiances (toujours), et celle de la météo, construite à
/// la demande d'après le temps qu'il fait (<see cref="ForWeather"/>). Les ambiances s'appuient sur l'analyse audio ;
/// les saisons aussi sur les mots du titre et de l'album, le genre et les tags MusicBrainz.
/// </summary>
public static class StationCatalog
{
    public static readonly IReadOnlyList<StationDefinition> Seasons =
    [
        new("halloween", RadioStationKinds.Season, "#e8590c", new SeasonWindow(10, 10, 11, 2), p => Max(
            Words(p, W.HalloweenWords) ? 1 : 0,
            Tagged(p, "halloween", "horror", "spooky", "horror punk") ? 1 : 0,
            GenreHas(p, "goth", "darkwave", "horror", "deathrock", "death rock", "industrial", "doom", "dark ambient", "witch house") ? 0.7 : 0,
            p.Feel is { Valence: < 0.35 } f ? 0.8 * (0.5 * Inv(f.Valence) + 0.25 * V(f.Aggressive) + 0.25 * V(f.Energy)) : 0)),

        new("christmas", RadioStationKinds.Season, "#c92a2a", new SeasonWindow(12, 1, 1, 6), p => Max(
            Words(p, W.ChristmasWords) ? 1 : 0,
            Tagged(p, "christmas", "christmas music", "xmas", "holiday", "christmas/holiday", "noel") ? 1 : 0)),

        new("new-year", RadioStationKinds.Season, "#f59f00", new SeasonWindow(12, 28, 1, 1), p => Clamp(
            Feel(p, f => 0.4 * V(f.Party) + 0.4 * V(f.Danceability) + 0.2 * V(f.Energy))
            + (Words(p, W.NewYearWords) ? 0.3 : 0)
            + (GenreHas(p, "dance", "disco", "house", "funk", "electro") ? 0.2 : 0)), Threshold: 0.65),

        new("winter", RadioStationKinds.Season, "#4c6ef5", new SeasonWindow(12, 1, 2, 28), p => Clamp(
            Feel(p, f => 0.35 * V(f.Relaxed) + 0.35 * V(f.Acoustic) + 0.3 * Inv(f.Energy))
            + (Words(p, W.WinterWords) ? 0.5 : 0))),

        new("valentine", RadioStationKinds.Season, "#e64980", new SeasonWindow(2, 7, 2, 14), p => Clamp(
            (Words(p, W.LoveWords) ? 0.6 : 0)
            + (Tagged(p, "love", "romantic", "love songs") ? 0.6 : 0)
            + Feel(p, f => 0.2 * V(f.Relaxed) + 0.2 * V(f.Happy)))),

        new("spring", RadioStationKinds.Season, "#40c057", new SeasonWindow(3, 20, 6, 14), p => Clamp(
            Feel(p, f => 0.35 * V(f.Happy) + 0.25 * V(f.Valence) + 0.2 * V(f.Acoustic) + (f.Energy is >= 0.3 and <= 0.7 ? 0.2 : 0))
            + (Words(p, W.SpringWords) ? 0.3 : 0))),

        new("summer", RadioStationKinds.Season, "#fab005", new SeasonWindow(6, 15, 8, 31), p => Clamp(
            (Words(p, W.SummerWords) ? 0.5 : 0)
            + (GenreHas(p, "reggae", "latin", "salsa", "bossa", "samba", "calypso", "ska", "dancehall", "tropical", "zouk", "afrobeat", "disco") ? 0.4 : 0)
            + Feel(p, f => 0.3 * V(f.Happy) + 0.25 * V(f.Party) + 0.25 * V(f.Danceability) + 0.2 * V(f.Valence)))),

        new("autumn", RadioStationKinds.Season, "#d9480f", new SeasonWindow(9, 22, 11, 30), p => Clamp(
            Feel(p, f => 0.3 * V(f.Acoustic) + 0.2 * V(f.Relaxed) + 0.25 * V(f.Sad) + 0.25 * Inv(f.Energy))
            + (Words(p, W.AutumnWords) ? 0.3 : 0)
            + (GenreHas(p, "folk", "singer songwriter", "jazz", "chanson") ? 0.15 : 0))),
    ];

    public static readonly IReadOnlyList<StationDefinition> Moods =
    [
        // Le genre seul ne suffit pas : sans analyse, toute la « pop » de la bibliothèque passerait, ballades tristes comprises.
        new("good-mood-pop", RadioStationKinds.Mood, "#ff922b", null, p => Feel(p, f =>
            (GenreHas(p, "pop", "dance", "disco", "funk", "synth", "variete", "motown", "soul", "new wave") ? 0.3 : 0)
            + 0.35 * V(f.Happy) + 0.2 * V(f.Valence) + 0.15 * V(f.Danceability)), Threshold: 0.55),

        new("chill", RadioStationKinds.Mood, "#74c0fc", null,
            p => Feel(p, f => 0.4 * V(f.Relaxed) + 0.3 * Inv(f.Energy) + 0.15 * V(f.Acoustic) + 0.15 * Inv(f.Aggressive)), Threshold: 0.65),

        new("energy", RadioStationKinds.Mood, "#fa5252", null,
            p => Feel(p, f => 0.4 * V(f.Energy) + 0.2 * V(f.Danceability) + 0.1 * V(f.Aggressive) + (f.Bpm is >= 115 and <= 160 ? 0.3 : 0)), Threshold: 0.65),

        new("party", RadioStationKinds.Mood, "#be4bdb", null, p => Clamp(
            Feel(p, f => 0.45 * V(f.Party) + 0.35 * V(f.Danceability) + 0.1 * V(f.Electronic) + 0.1 * V(f.Happy))
            + (p.Feel is not null && GenreHas(p, "dance", "house", "disco", "electro", "edm", "techno", "funk") ? 0.15 : 0))),

        new("melancholy", RadioStationKinds.Mood, "#5c7cfa", null,
            p => Feel(p, f => 0.5 * V(f.Sad) + 0.3 * Inv(f.Valence) + 0.2 * Inv(f.Energy))),

        new("focus", RadioStationKinds.Mood, "#20c997", null,
            p => Feel(p, f => 0.5 * V(f.Instrumental) + 0.2 * V(f.Relaxed) + 0.15 * Inv(f.Energy) + 0.15 * Inv(f.Aggressive)), Threshold: 0.65),
    ];

    public const string WeatherId = "weather";

    /// <summary>Radio du temps qu'il fait : son ambiance dépend du ciel, de la température et du jour.</summary>
    public static StationDefinition ForWeather(WeatherNow weather)
    {
        Func<TrackProfile, double> score = Category(weather) switch
        {
            WeatherCategory.Sunny => p => Feel(p, f => 0.35 * V(f.Happy) + 0.25 * V(f.Valence) + 0.2 * V(f.Danceability) + 0.2 * V(f.Energy)),
            WeatherCategory.Bright => p => Feel(p, f => 0.35 * V(f.Happy) + 0.25 * V(f.Valence) + 0.2 * V(f.Acoustic) + 0.2 * V(f.Relaxed)),
            WeatherCategory.Cloudy => p => Feel(p, f => 0.4 * V(f.Relaxed) + 0.3 * V(f.Acoustic) + 0.3 * Inv(f.Aggressive)),
            WeatherCategory.Fog => p => Feel(p, f => 0.4 * V(f.Instrumental) + 0.4 * V(f.Relaxed) + 0.2 * Inv(f.Energy)),
            WeatherCategory.Rain => p => Clamp(Feel(p, f => 0.3 * V(f.Acoustic) + 0.3 * V(f.Relaxed) + 0.2 * V(f.Sad) + 0.2 * Inv(f.Energy))
                + (Words(p, W.RainWords) ? 0.3 : 0)),
            WeatherCategory.Snow => p => Clamp(Feel(p, f => 0.35 * V(f.Relaxed) + 0.35 * V(f.Acoustic) + 0.3 * Inv(f.Energy))
                + (Words(p, W.WinterWords) ? 0.3 : 0)),
            _ => p => Clamp(Feel(p, f => 0.4 * V(f.Energy) + 0.3 * V(f.Aggressive) + 0.3 * Inv(f.Valence))
                + (Words(p, W.StormWords) ? 0.3 : 0)),
        };
        // La nuit, plus doux.
        var final = weather.IsDay ? score : p => 0.85 * score(p) + 0.15 * (p.Feel is { } f ? Inv(f.Energy) : 0);
        return new StationDefinition(WeatherId, RadioStationKinds.Weather, Color(Category(weather)), null, final);
    }

    public static WeatherCategory Category(WeatherNow w) => w.Code switch
    {
        0 or 1 => w.TemperatureC >= 20 && w.IsDay ? WeatherCategory.Sunny : WeatherCategory.Bright,
        2 or 3 => WeatherCategory.Cloudy,
        45 or 48 => WeatherCategory.Fog,
        >= 71 and <= 77 or 85 or 86 => WeatherCategory.Snow,
        >= 95 => WeatherCategory.Storm,
        _ => WeatherCategory.Rain,
    };

    private static string Color(WeatherCategory c) => c switch
    {
        WeatherCategory.Sunny => "#fab005",
        WeatherCategory.Bright => "#4dabf7",
        WeatherCategory.Cloudy => "#868e96",
        WeatherCategory.Fog => "#adb5bd",
        WeatherCategory.Rain => "#4263eb",
        WeatherCategory.Snow => "#74c0fc",
        _ => "#5f3dc4",
    };

    public static StationDefinition? Find(string id) =>
        Seasons.Concat(Moods).FirstOrDefault(s => s.Id == id);

    private static double V(double? x) => x ?? 0;
    private static double Inv(double? x) => x is { } v ? 1 - v : 0;
    private static double Clamp(double x) => Math.Clamp(x, 0, 1);
    private static double Max(params double[] xs) => xs.Max();
    /// <summary>Note tirée de l'analyse audio ; 0 pour un morceau pas encore analysé.</summary>
    private static double Feel(TrackProfile p, Func<AudioFeel, double> f) => p.Feel is { } feel ? f(feel) : 0;
    private static bool Words(TrackProfile p, Regex words) => words.IsMatch(p.Text);
    private static bool Tagged(TrackProfile p, params string[] tags) => tags.Any(p.Tags.Contains);
    private static bool GenreHas(TrackProfile p, params string[] fragments) => p.Genre.Length > 0 && fragments.Any(p.Genre.Contains);

    private static Regex WordList(params string[] words) =>
        new(@"(?<![\p{L}\d])(" + string.Join('|', words.Select(w => Regex.Escape(TrackProfile.Fold(w)))) + @")(?![\p{L}\d])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Listes de mots, à part : prêtes avant les stations qui s'en servent.</summary>
    private static class W
    {
        public static readonly Regex HalloweenWords = WordList("halloween", "ghost", "ghosts", "ghoul", "zombie", "zombies", "witch", "witches",
            "vampire", "vampires", "monster", "monsters", "spooky", "haunted", "horror", "devil", "demon", "demons", "skeleton", "graveyard",
            "cemetery", "fantome", "fantomes", "sorciere", "sorcieres", "monstre", "monstres", "diable", "thriller", "dracula", "werewolf",
            "loup garou", "frankenstein", "pumpkin", "citrouille", "boo", "creepy", "nightmare", "cauchemar", "tombe", "cimetiere");

        public static readonly Regex ChristmasWords = WordList("christmas", "xmas", "noel", "santa", "santa claus", "jingle", "jingle bells", "sleigh",
            "reindeer", "rudolph", "mistletoe", "navidad", "weihnacht", "weihnachten", "silent night", "douce nuit", "petit papa", "papa noel",
            "white christmas", "let it snow", "feliz navidad", "holly", "nativity", "deck the halls", "tannenbaum", "sapin", "pere noel",
            "il est ne", "adeste fideles", "minuit chretiens", "winter wonderland");

        public static readonly Regex NewYearWords = WordList("new year", "new year's", "nouvel an", "bonne annee", "party", "fete", "celebrate",
            "celebration", "midnight", "minuit", "champagne", "countdown", "auld lang syne");

        public static readonly Regex WinterWords = WordList("winter", "hiver", "snow", "neige", "cold", "froid", "december", "decembre", "january",
            "janvier", "frozen", "ice", "glace", "fireplace", "cheminee", "blizzard", "flocon", "snowflake");

        public static readonly Regex LoveWords = WordList("love", "lover", "amour", "amoureux", "amoureuse", "heart", "coeur", "kiss", "baiser",
            "darling", "cherie", "valentine", "romance", "romantic", "je t'aime", "t'aime", "my baby", "sweetheart", "be mine", "te quiero", "amore");

        public static readonly Regex SpringWords = WordList("spring", "printemps", "flower", "flowers", "fleur", "fleurs", "bloom", "blossom", "bird",
            "birds", "oiseau", "oiseaux", "april", "avril", "may", "mai", "sunshine", "garden", "jardin");

        public static readonly Regex SummerWords = WordList("summer", "summertime", "ete", "sun", "soleil", "beach", "plage", "sea", "mer", "ocean",
            "island", "ile", "tropical", "vacation", "vacances", "holiday", "holidays", "july", "juillet", "august", "aout", "surf", "sunny",
            "palm", "bikini", "sable", "sand");

        public static readonly Regex AutumnWords = WordList("autumn", "automne", "fall", "rain", "pluie", "leaves", "feuilles", "feuilles mortes",
            "october", "octobre", "november", "novembre", "september", "septembre", "grey", "gris", "brume", "mist");

        public static readonly Regex RainWords = WordList("rain", "rainy", "raining", "pluie", "pleut", "umbrella", "parapluie", "storm", "drops", "gouttes");

        public static readonly Regex StormWords = WordList("storm", "thunder", "thunderstorm", "lightning", "orage", "tempete", "tonnerre", "eclair", "hurricane");
    }
}

public enum WeatherCategory { Sunny, Bright, Cloudy, Fog, Rain, Snow, Storm }

/// <summary>Temps qu'il fait : code météo WMO, température, jour ou nuit, lieu affiché.</summary>
public sealed record WeatherNow(int Code, double TemperatureC, bool IsDay, string Place);
