using System.Text.RegularExpressions;

namespace SonNuage.Server.Library;

/// <summary>
/// Crédit d'un morceau découpé en artiste principal et invités : « OrelSan feat. Damso » appartient à OrelSan,
/// Damso y est invité. Prudent : « feat. », « ft. », « featuring » sont toujours coupés ; « & », « x », « avec »,
/// « with » et la virgule seulement quand le premier nom est l'artiste de l'album (« Simon & Garfunkel » reste entier).
/// Les invités cités dans le titre, entre parenthèses ou crochets (« Titre (feat. X) »), comptent aussi.
/// </summary>
public sealed partial record ArtistCredit(string Main, IReadOnlyList<string> Guests)
{
    public static ArtistCredit Parse(string credit, string albumArtist, string title)
    {
        var (main, guests) = SplitCredit(credit.Trim(), albumArtist);
        var fromTitle = TitleFeaturing().Matches(title).SelectMany(m => SplitGuests(m.Groups[1].Value));
        var mainKey = NameNormalizer.Key(main);
        var kept = guests.Concat(fromTitle)
            .Where(g => NameNormalizer.Key(g) != mainKey)
            .DistinctBy(NameNormalizer.Key)
            .ToList();
        return new ArtistCredit(main, kept);
    }

    private static (string Main, IEnumerable<string> Guests) SplitCredit(string credit, string albumArtist)
    {
        if (Featuring().Match(credit) is { Success: true } m)
        {
            var main = credit[..m.Index].Trim();
            return main.Length > 0 ? (main, SplitGuests(credit[(m.Index + m.Length)..])) : (credit, []);
        }
        var parts = GuestSeparator().Split(credit);
        var albumKey = NameNormalizer.Key(albumArtist);
        if (parts.Length > 1 && NameNormalizer.Key(parts[0]) == albumKey && NameNormalizer.Key(credit) != albumKey)
            return (parts[0].Trim(), SplitGuests(string.Join(", ", parts[1..])));
        return (credit, []);
    }

    private static IEnumerable<string> SplitGuests(string guests) =>
        GuestSeparator().Split(guests).Select(g => g.Trim(' ', '(', ')', '[', ']')).Where(g => g.Length > 0);

    /// <summary>« feat. », « ft. », « featuring », éventuellement après une parenthèse ou un crochet ouvrant.</summary>
    [GeneratedRegex(@"\s*[\(\[]?\s*(?<![\p{L}\p{N}])(?:feat\.?|ft\.?|featuring)(?=\s)", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s*(?:,|&|\s[xX]\s|\savec\s|\swith\s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex GuestSeparator();

    [GeneratedRegex(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring)\s+([^\)\]]+)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleFeaturing();
}
