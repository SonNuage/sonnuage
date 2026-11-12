using System.Globalization;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Styles Discogs d'un morceau, tels que les donne la tête « genre_discogs400 » d'Essentia : chaque style est rangé sous
/// un grand genre (« Rock---Pop Rock »), avec une activation de 0 à 1 (les styles ne s'excluent pas). Stockés en un
/// seul texte, le plus probable d'abord : <c>Rock---Pop Rock:0.623|Pop---Ballad:0.21</c>.
/// </summary>
public static class DiscogsStyles
{
    /// <summary>Styles gardés par morceau.</summary>
    public const int Kept = 5;
    public const int MaxLength = 400;
    private const string Separator = "---";

    public sealed record Style(string Genre, string Name, double Score);

    /// <summary>Texte canonique des styles valides donnés ; null s'il n'en reste aucun.</summary>
    public static string? Encode(IEnumerable<(string Label, double Score)> styles)
    {
        var valid = styles
            .Where(s => s.Score is >= 0 and <= 1 && Split(s.Label) is not null && !s.Label.Contains('|') && !s.Label.Contains(':'))
            .OrderByDescending(s => s.Score).Take(Kept)
            .Select(s => $"{s.Label}:{Math.Round(s.Score, 3).ToString(CultureInfo.InvariantCulture)}")
            .ToList();
        return valid.Count == 0 ? null : string.Join('|', valid);
    }

    /// <summary>Styles d'un texte canonique, le plus probable d'abord.</summary>
    public static IReadOnlyList<Style> Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var list = new List<Style>();
        foreach (var entry in text.Split('|'))
        {
            var colon = entry.LastIndexOf(':');
            if (colon < 0 || Split(entry[..colon]) is not { } parts
                || !double.TryParse(entry[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)) continue;
            list.Add(new Style(parts.Genre, parts.Name, score));
        }
        return list;
    }

    private static (string Genre, string Name)? Split(string label)
    {
        var at = label.IndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0 || at + Separator.Length >= label.Length) return null;
        return (label[..at].Trim(), label[(at + Separator.Length)..].Trim());
    }
}
