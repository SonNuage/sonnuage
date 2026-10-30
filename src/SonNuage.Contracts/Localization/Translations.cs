using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SonNuage.Contracts.Localization;

/// <summary>
/// Textes de l'application, une langue par fichier <c>Localization/{code}.json</c> intégré à cet assembly
/// (partagé par le serveur, le web et Android). Ajouter une langue = ajouter un fichier : elle apparaît d'elle-même.
/// <list type="bullet">
/// <item>Clés hiérarchiques (<c>"player": { "next": "Suivant" }</c> → <c>player.next</c>).</item>
/// <item>Paramètres nommés : <c>"Bonjour {name}"</c>.</item>
/// <item>Pluriels selon le paramètre <c>count</c> : <c>{ "one": "{count} titre", "other": "{count} titres" }</c>
/// (catégories CLDR : zero, one, two, few, many, other).</item>
/// <item>Texte manquant : repli sur l'anglais, puis sur la clé.</item>
/// </list>
/// Sans dépendre de CultureInfo : le WebAssembly tourne en globalisation invariante.
/// </summary>
public sealed class Translations
{
    public const string Fallback = "en";
    private const string Prefix = "SonNuage.Contracts.Localization.";

    private static readonly Lazy<Translations> Shared = new(() => new Translations());

    private readonly Dictionary<string, Language> _languages;
    private readonly ConcurrentDictionary<string, Language> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public static Translations Instance => Shared.Value;

    private Translations()
    {
        var assembly = typeof(Translations).Assembly;
        _languages = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => Load(assembly, n, n[Prefix.Length..^".json".Length]))
            .ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Langues disponibles : code, nom dans la langue elle-même.</summary>
    public IReadOnlyList<LanguageInfo> Available =>
        _languages.Values.OrderBy(l => l.Name, StringComparer.Ordinal).Select(l => new LanguageInfo(l.Code, l.Name)).ToList();

    public bool IsAvailable(string? code) => code is not null && _languages.ContainsKey(code);

    /// <summary>
    /// Meilleure langue disponible pour une liste de préférences (« fr-CA », « en-US,en;q=0.9 »…) :
    /// correspondance exacte, puis sur la langue seule ; l'anglais sinon.
    /// </summary>
    public string Resolve(params string?[] preferences)
    {
        foreach (var raw in preferences)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var part in raw.Split(','))
            {
                var tag = part.Split(';')[0].Trim().Replace('_', '-');
                if (tag.Length == 0) continue;
                if (_languages.ContainsKey(tag)) return _languages[tag].Code;
                var primary = tag.Split('-')[0];
                if (_languages.TryGetValue(primary, out var l)) return l.Code;
            }
        }
        return Fallback;
    }

    public string Get(string language, string key, params (string Name, object? Value)[] args)
    {
        var lang = Find(language);
        var value = Lookup(lang, key, args) ?? (lang.Code == Fallback ? null : Lookup(Find(Fallback), key, args, lang));
        return value is null ? key : Fill(value, args, lang);
    }

    public bool Has(string language, string key) => Find(language).Texts.ContainsKey(key) || Find(language).Plurals.ContainsKey(key);

    public IReadOnlyCollection<string> Keys(string language)
    {
        var l = Find(language);
        return l.Texts.Keys.Concat(l.Plurals.Keys).ToList();
    }

    public LanguageFormat Format(string language) => Find(language).Format;

    private Language Find(string code) =>
        _languages.TryGetValue(code, out var l) ? l
        : _resolved.GetOrAdd(code, c => _languages[Resolve(c)]);

    private static string? Lookup(Language lang, string key, (string Name, object? Value)[] args, Language? pluralRulesOf = null)
    {
        if (lang.Texts.TryGetValue(key, out var text)) return text;
        if (!lang.Plurals.TryGetValue(key, out var forms)) return null;
        var count = args.FirstOrDefault(a => a.Name == "count").Value;
        var n = count is null ? 0 : Convert.ToDouble(count, CultureInfo.InvariantCulture);
        // Les règles de pluriel sont celles de la langue affichée, même quand le texte vient du repli.
        var category = PluralRules.Category((pluralRulesOf ?? lang).Code, n);
        return forms.GetValueOrDefault(category) ?? forms.GetValueOrDefault("other") ?? forms.Values.FirstOrDefault();
    }

    private static string Fill(string text, (string Name, object? Value)[] args, Language lang)
    {
        if (args.Length == 0 || !text.Contains('{')) return text;
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var close = text[i] == '{' ? text.IndexOf('}', i + 1) : -1;
            if (close < 0)
            {
                sb.Append(text[i]);
                continue;
            }
            var name = text[(i + 1)..close];
            var found = Array.FindIndex(args, a => a.Name == name);
            if (found < 0)
            {
                sb.Append(text[i]);
                continue;
            }
            sb.Append(args[found].Value switch
            {
                int or long => lang.Format.Number(Convert.ToDouble(args[found].Value, CultureInfo.InvariantCulture), 0),
                null => "",
                var v => v.ToString(),
            });
            i = close;
        }
        return sb.ToString();
    }

    private static Language Load(Assembly assembly, string resource, string code)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var plurals = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var meta = doc.RootElement.TryGetProperty("_meta", out var m) ? m : default;
        Flatten(doc.RootElement, "", texts, plurals);
        string Meta(string name, string fallback) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        string[] List(string name) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
        var format = new LanguageFormat(
            Meta("decimalSeparator", "."), Meta("groupSeparator", ","),
            List("months"), List("shortMonths"), List("days"), List("shortDays"));
        return new Language(code, Meta("name", code), texts, plurals, format);
    }

    private static readonly HashSet<string> PluralCategories = ["zero", "one", "two", "few", "many", "other"];

    private static void Flatten(JsonElement e, string prefix, Dictionary<string, string> texts, Dictionary<string, Dictionary<string, string>> plurals)
    {
        foreach (var p in e.EnumerateObject())
        {
            if (prefix.Length == 0 && p.Name == "_meta") continue;
            var key = prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}";
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.String:
                    texts[key] = p.Value.GetString()!;
                    break;
                case JsonValueKind.Object when p.Value.EnumerateObject().Any() && p.Value.EnumerateObject().All(c => PluralCategories.Contains(c.Name) && c.Value.ValueKind == JsonValueKind.String):
                    plurals[key] = p.Value.EnumerateObject().ToDictionary(c => c.Name, c => c.Value.GetString()!);
                    break;
                case JsonValueKind.Object:
                    Flatten(p.Value, key, texts, plurals);
                    break;
            }
        }
    }

    private sealed record Language(
        string Code, string Name, Dictionary<string, string> Texts, Dictionary<string, Dictionary<string, string>> Plurals, LanguageFormat Format);
}

public sealed record LanguageInfo(string Code, string Name);

/// <summary>Formats propres à une langue (nombres, mois, jours), lus dans la section <c>_meta</c> de son fichier.</summary>
public sealed record LanguageFormat(
    string DecimalSeparator, string GroupSeparator,
    IReadOnlyList<string> Months, IReadOnlyList<string> ShortMonths, IReadOnlyList<string> Days, IReadOnlyList<string> ShortDays)
{
    public string Number(double value, int decimals = 0)
    {
        var s = Math.Round(Math.Abs(value), decimals).ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture);
        var dot = s.IndexOf('.');
        var integer = dot < 0 ? s : s[..dot];
        var sb = new StringBuilder();
        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0) sb.Append(GroupSeparator);
            sb.Append(integer[i]);
        }
        if (dot >= 0) sb.Append(DecimalSeparator).Append(s[(dot + 1)..]);
        return (value < 0 && sb.ToString().Any(c => c is >= '1' and <= '9') ? "-" : "") + sb;
    }

    public string Month(int month) => Months.Count >= 12 ? Months[month - 1] : month.ToString(CultureInfo.InvariantCulture);
    public string ShortMonth(int month) => ShortMonths.Count >= 12 ? ShortMonths[month - 1] : Month(month);
    /// <summary>Jour de la semaine, 0 = dimanche (comme <see cref="DayOfWeek"/>).</summary>
    public string Day(DayOfWeek day) => Days.Count >= 7 ? Days[(int)day] : day.ToString();
    public string ShortDay(DayOfWeek day) => ShortDays.Count >= 7 ? ShortDays[(int)day] : Day(day);
}

/// <summary>Catégorie de pluriel CLDR par langue. Une langue absente suit la règle anglaise (1 → one, sinon other).</summary>
public static class PluralRules
{
    public static string Category(string language, double n)
    {
        var i = Math.Floor(Math.Abs(n));
        var isInt = i == Math.Abs(n);
        return language.Split('-')[0].ToLowerInvariant() switch
        {
            // Français, portugais : 0 et 1 au singulier ; les grands nombres ronds au « many ».
            "fr" or "pt" => i is 0 or 1 ? "one" : isInt && i != 0 && i % 1_000_000 == 0 ? "many" : "other",
            // Langues sans pluriel grammatical.
            "ja" or "zh" or "ko" or "vi" or "th" or "id" => "other",
            "ru" or "uk" when isInt => (i % 10, i % 100) switch
            {
                (1, not 11) => "one",
                ( >= 2 and <= 4, < 12 or > 14) => "few",
                _ => "many",
            },
            "pl" when isInt => i == 1 ? "one" : (i % 10 is >= 2 and <= 4) && (i % 100 is < 12 or > 14) ? "few" : "many",
            "ru" or "uk" or "pl" => "other",
            _ => isInt && i == 1 ? "one" : "other",
        };
    }
}
