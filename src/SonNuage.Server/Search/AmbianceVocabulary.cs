using SonNuage.Server.Radio;

namespace SonNuage.Server.Search;

/// <summary>
/// Vocabulaire de la recherche par ambiance, en français puis en anglais. Pour l'enrichir : ajouter des formes à une
/// ligne de <see cref="Words"/> (accents et majuscules indifférents), ou une ambiance à la liste et sa ligne de mots.
/// <list type="bullet">
/// <item><see cref="AmbianceMood"/> : ce que l'ambiance demande à l'analyse audio, et les tags ou genres qui la signalent
///   pour un morceau pas encore analysé.</item>
/// <item><see cref="Words"/> : mots d'humeur, d'énergie, de tempo et d'activité (« travailler » → concentration).</item>
/// <item><see cref="Stopwords"/> : mots de liaison ignorés (« musique », « pour », « something »…).</item>
/// <item><see cref="GenreAliases"/> : noms de genres courants et leurs équivalents dans les tags.</item>
/// </list>
/// </summary>
public static class AmbianceVocabulary
{
    public static readonly AmbianceMood Calm = new("calm",
        f => 0.4 * V(f.Relaxed) + 0.3 * Inv(f.Energy) + 0.15 * V(f.Acoustic) + 0.15 * Inv(f.Aggressive),
        ["chill", "chillout", "chill out", "calm", "relaxing", "relax", "mellow", "ambient", "downtempo", "lounge", "easy listening", "soft"],
        ["ambient", "lounge", "chill", "downtempo", "new age", "easy listening"]);

    public static readonly AmbianceMood Focus = new("focus",
        f => 0.5 * V(f.Instrumental) + 0.2 * V(f.Relaxed) + 0.15 * Inv(f.Energy) + 0.15 * Inv(f.Aggressive),
        ["instrumental", "ambient", "focus", "study", "lo fi", "lofi", "post rock", "minimal"],
        ["ambient", "lo fi", "lofi", "post rock", "classical", "classique", "minimal"]);

    public static readonly AmbianceMood Sleep = new("sleep",
        f => 0.35 * V(f.Relaxed) + 0.35 * Inv(f.Energy) + 0.15 * V(f.Instrumental) + 0.15 * Slow(f.Bpm),
        ["sleep", "lullaby", "ambient", "relaxing"],
        ["ambient", "new age", "lullaby"]);

    public static readonly AmbianceMood Energetic = new("energetic",
        f => 0.5 * V(f.Energy) + 0.2 * V(f.Danceability) + 0.1 * V(f.Aggressive) + 0.2 * Fast(f.Bpm, 115),
        ["energetic", "upbeat", "high energy", "powerful"],
        []);

    public static readonly AmbianceMood Workout = new("workout",
        f => 0.45 * V(f.Energy) + 0.2 * V(f.Danceability) + 0.35 * (f.Bpm is >= 120 and <= 185 ? 1 : 0),
        ["workout", "running", "gym", "training", "motivation"],
        []);

    public static readonly AmbianceMood Happy = new("happy",
        f => 0.5 * V(f.Happy) + 0.3 * V(f.Valence) + 0.2 * V(f.Danceability),
        ["happy", "feel good", "feelgood", "uplifting", "cheerful", "fun", "summer"],
        []);

    public static readonly AmbianceMood Sad = new("sad",
        f => 0.5 * V(f.Sad) + 0.3 * Inv(f.Valence) + 0.2 * Inv(f.Energy),
        ["sad", "melancholic", "melancholy", "melancolie", "depressing", "sorrow", "heartbreak", "breakup"],
        []);

    public static readonly AmbianceMood Party = new("party",
        f => 0.45 * V(f.Party) + 0.35 * V(f.Danceability) + 0.1 * V(f.Electronic) + 0.1 * V(f.Happy),
        ["party", "club", "dance", "dancefloor"],
        ["dance", "house", "disco", "edm", "club"]);

    public static readonly AmbianceMood Dance = new("dance",
        f => 0.6 * V(f.Danceability) + 0.2 * V(f.Party) + 0.2 * V(f.Energy),
        ["dance", "danceable", "groovy", "dancefloor"],
        ["dance", "disco", "funk", "house"]);

    public static readonly AmbianceMood Aggressive = new("aggressive",
        f => 0.6 * V(f.Aggressive) + 0.4 * V(f.Energy),
        ["aggressive", "angry", "brutal"],
        ["metal", "hardcore", "thrash", "grindcore"]);

    public static readonly AmbianceMood Romantic = new("romantic",
        f => 0.3 * V(f.Relaxed) + 0.25 * V(f.Happy) + 0.25 * V(f.Acoustic) + 0.2 * Inv(f.Aggressive),
        ["love", "romantic", "love songs", "romance", "sensual"],
        []);

    public static readonly AmbianceMood Acoustic = new("acoustic",
        f => 0.8 * V(f.Acoustic) + 0.2 * Inv(f.Electronic),
        ["acoustic", "unplugged"],
        ["acoustic", "folk", "unplugged"]);

    public static readonly AmbianceMood Instrumental = new("instrumental",
        f => V(f.Instrumental),
        ["instrumental"],
        ["instrumental", "classical", "classique", "soundtrack"]);

    public static readonly AmbianceMood Electronic = new("electronic",
        f => 0.8 * V(f.Electronic) + 0.2 * Inv(f.Acoustic),
        ["electronic", "electronica", "electro"],
        ["electro", "electronic", "electronica", "techno", "house", "edm", "synth"]);

    public static readonly AmbianceMood SlowTempo = new("slow",
        f => 0.6 * Slow(f.Bpm) + 0.4 * Inv(f.Energy),
        ["slow"],
        ["downtempo"]);

    public static readonly AmbianceMood FastTempo = new("fast",
        f => 0.6 * Fast(f.Bpm, 130) + 0.4 * V(f.Energy),
        ["fast", "uptempo"],
        []);

    /// <summary>Expressions reconnues et ambiances demandées.</summary>
    public static readonly IReadOnlyList<AmbianceWords> Words =
    [
        new([Calm], "calme", "calmes", "tranquille", "tranquilles", "paisible", "paisibles", "doux", "douce", "douces", "apaisant",
            "apaisante", "apaisantes", "relaxant", "relaxante", "relaxantes", "détente", "détendu", "détendue", "zen", "serein", "sereine",
            "cosy", "chill", "chiller", "chillout", "posé", "posée",
            "calm", "peaceful", "relaxing", "relaxed", "relax", "soothing", "mellow", "soft", "chilled", "laid back", "cozy", "quiet"),
        new([Calm], "dimanche", "soir", "nuit", "yoga", "méditation", "méditer", "bain", "massage",
            "sunday", "lazy", "evening", "night", "late night", "meditation", "meditate", "bath", "spa"),
        new([Focus], "travailler", "travail", "bosser", "boulot", "bureau", "concentration", "concentrer", "concentré", "étudier", "étude",
            "études", "réviser", "révisions", "devoirs", "coder", "programmer", "lire", "lecture", "focus",
            "work", "working", "office", "concentrate", "focused", "study", "studying", "homework", "coding", "programming", "reading", "read"),
        new([Sleep], "dormir", "sommeil", "endormir", "sieste", "berceuse", "berceuses", "coucher",
            "sleep", "sleeping", "asleep", "nap", "lullaby", "lullabies", "bedtime"),
        new([Energetic], "énergique", "énergiques", "énergie", "dynamique", "dynamiques", "pêchu", "pêchue", "pêchus", "entraînant",
            "entraînante", "entraînants", "entraînantes", "motivant", "motivante", "puissant", "puissante", "intense", "intenses", "tonique",
            "energetic", "energy", "upbeat", "punchy", "powerful", "pumped", "pump up", "motivating", "motivation", "hype"),
        new([Workout], "courir", "course", "footing", "jogging", "sport", "sportif", "muscu", "musculation", "entraînement", "salle",
            "fitness", "cardio", "vélo",
            "run", "running", "runner", "workout", "training", "exercise", "cycling", "gym"),
        new([Happy], "joyeux", "joyeuse", "joyeuses", "gai", "gaie", "gais", "gaies", "heureux", "heureuse", "bonne humeur", "positif",
            "positive", "positives", "ensoleillé", "ensoleillée", "optimiste", "enjoué", "enjouée",
            "happy", "cheerful", "joyful", "feel good", "feelgood", "good mood", "uplifting", "sunny", "fun"),
        new([Happy], "été", "estival", "estivale", "vacances", "plage", "matin", "cuisiner",
            "summer", "summertime", "holidays", "vacation", "beach", "morning", "cooking"),
        new([Happy, Energetic], "voiture", "route", "conduire", "road trip", "driving", "drive"),
        new([Sad], "triste", "tristes", "mélancolique", "mélancoliques", "mélancolie", "nostalgique", "nostalgiques", "nostalgie",
            "déprimant", "déprimante", "déprime", "chagrin", "rupture", "larmes", "pleurer", "cafard", "spleen", "sombre", "sombres",
            "sad", "sadness", "melancholy", "melancholic", "nostalgic", "depressing", "heartbreak", "heartbroken", "breakup", "crying",
            "gloomy", "dark"),
        new([Sad, Calm], "pluie", "pluvieux", "pluvieuse", "automne", "rainy", "autumn"),
        new([Party], "fête", "fêtes", "soirée", "soirées", "festif", "festive", "festifs", "festives", "apéro", "apéritif", "boîte",
            "anniversaire", "clubbing",
            "party", "parties", "partying", "club", "celebration", "celebrate", "birthday"),
        new([Dance], "dansant", "dansante", "dansants", "dansantes", "danser", "danse",
            "dance", "dancing", "danceable", "groove", "groovy"),
        new([Aggressive], "agressif", "agressive", "agressifs", "rageur", "rageuse", "colère", "énervé", "énervée", "violent", "violente",
            "brutal", "brutale", "défouler", "furieux",
            "angry", "aggressive", "rage", "furious"),
        new([Romantic], "romantique", "romantiques", "amour", "amoureux", "amoureuse", "tendre", "tendresse", "sensuel", "sensuelle",
            "saint valentin",
            "romantic", "romance", "love", "love songs", "sensual", "sexy", "valentine", "date night"),
        new([Acoustic], "acoustique", "acoustiques", "guitare sèche", "acoustic", "unplugged"),
        new([Instrumental], "instrumental", "instrumentale", "instrumentaux", "instrumentales", "sans paroles", "sans voix",
            "instrumentals", "no lyrics", "without lyrics", "no vocals"),
        new([Electronic], "électronique", "électroniques", "synthétique", "electronic"),
        new([SlowTempo], "lent", "lente", "lents", "lentes", "tempo lent", "doucement", "slow", "slowly", "slow tempo"),
        new([FastTempo], "rapide", "rapides", "tempo rapide", "vite", "fast", "quick", "uptempo", "up tempo"),
    ];

    /// <summary>Mots ignorés. « pas » n'y est pas : « pas triste » ne doit pas donner une ambiance triste.</summary>
    public static readonly IReadOnlyList<string> Stopwords =
    [
        "musique", "musiques", "morceau", "morceaux", "chanson", "chansons", "titre", "titres", "son", "sons", "playlist", "truc", "trucs",
        "quelque", "chose", "un", "une", "des", "de", "du", "le", "la", "les", "pour", "avec", "et", "ou", "en", "au", "aux", "à",
        "pendant", "quand", "qui", "que", "se", "me", "te", "sur", "dans", "peu", "très", "plutôt", "assez", "bien", "super", "vraiment",
        "ambiance", "ambiances", "style", "ce", "cette", "ces", "mon", "ma", "mes", "faire", "jour", "journée", "moment", "ça", "écouter",
        "je", "veux", "envie", "besoin", "fond",
        "music", "song", "songs", "track", "tracks", "tune", "tunes", "something", "some", "the", "a", "an", "for", "to", "with", "and",
        "or", "of", "in", "on", "while", "during", "when", "my", "me", "i", "very", "really", "quite", "pretty", "bit", "little", "vibe",
        "vibes", "mood", "kind", "day", "days", "time", "stuff", "listen", "listening", "want", "need", "background",
    ];

    /// <summary>Genres courants (forme saisie → fragments cherchés dans le genre du fichier et les tags).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> GenreAliases = new Dictionary<string, string[]>
    {
        ["rock"] = ["rock"], ["pop"] = ["pop"], ["jazz"] = ["jazz"], ["blues"] = ["blues"], ["folk"] = ["folk"], ["soul"] = ["soul"],
        ["funk"] = ["funk"], ["reggae"] = ["reggae"], ["disco"] = ["disco"], ["punk"] = ["punk"], ["metal"] = ["metal"],
        ["country"] = ["country"], ["techno"] = ["techno"], ["house"] = ["house"], ["ambient"] = ["ambient"], ["grunge"] = ["grunge"],
        ["électro"] = ["electro", "electronic"], ["classique"] = ["classical", "classique"], ["classical"] = ["classical", "classique"],
        ["rap"] = ["rap", "hip hop"], ["hip hop"] = ["hip hop", "rap"], ["hiphop"] = ["hip hop", "rap"],
        ["variété"] = ["variete", "chanson"], ["chanson française"] = ["chanson"], ["world"] = ["world"], ["latino"] = ["latin"],
        ["latin"] = ["latin"], ["lo-fi"] = ["lo fi", "lofi"], ["lofi"] = ["lo fi", "lofi"], ["bossa nova"] = ["bossa"],
        ["bande originale"] = ["soundtrack"], ["musique de film"] = ["soundtrack"], ["soundtrack"] = ["soundtrack"], ["indé"] = ["indie"],
        ["indie"] = ["indie"], ["trip hop"] = ["trip hop"], ["new wave"] = ["new wave"], ["dub"] = ["dub"], ["r&b"] = ["r b", "rnb"],
    };

    /// <summary>Décennies en toutes lettres.</summary>
    public static readonly IReadOnlyDictionary<string, int> DecadeWords = new Dictionary<string, int>
    {
        ["fifties"] = 1950, ["sixties"] = 1960, ["seventies"] = 1970, ["eighties"] = 1980, ["nineties"] = 1990,
    };

    private static double V(double? x) => x ?? 0;
    private static double Inv(double? x) => x is { } v ? 1 - v : 0;
    private static double Slow(double? bpm) => bpm switch { <= 95 => 1, <= 110 => 0.5, _ => 0 };
    private static double Fast(double? bpm, double from) => bpm is { } b ? b >= from ? 1 : b >= from - 15 ? 0.5 : 0 : 0;
}

/// <summary>Une ligne du vocabulaire : des formes (FR et EN) qui demandent les mêmes ambiances.</summary>
public sealed record AmbianceWords(IReadOnlyList<AmbianceMood> Moods, params string[] Forms);
