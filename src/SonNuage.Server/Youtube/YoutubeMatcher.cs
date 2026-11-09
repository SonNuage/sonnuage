using System.Globalization;
using System.Text;

namespace SonNuage.Server.Youtube;

/// <summary>Ce qu'on cherche : l'artiste, le titre, et la durée attendue quand on la connaît (MusicBrainz).</summary>
public sealed record MatchQuery(string Artist, string Title, double? DurationSeconds);

public sealed record RankedEntry(YtEntry Entry, int Score);

/// <summary>
/// Note sur 100 d'un résultat YouTube pour « artiste + titre » : mots du titre retrouvés (45), artiste dans la chaîne
/// ou le titre (30, +5 pour une chaîne « - Topic »), durée proche (20, ou 10 si inconnue). Mots en trop : −2 chacun
/// (−10 au plus). Versions live, remix, karaoké, reprise… non demandées : −45.
/// </summary>
public static class YoutubeMatcher
{
    /// <summary>Au-dessus, la correspondance est retenue d'office ; en dessous, on propose les candidats au choix.</summary>
    public const int SureScore = 80;

    // Mots d'habillage d'un titre YouTube, sans rapport avec le morceau.
    private static readonly HashSet<string> Noise =
    [
        "official", "officiel", "officielle", "video", "audio", "clip", "lyrics", "lyric", "paroles", "hd", "hq", "4k",
        "remastered", "remaster", "music", "musique", "visualizer", "visualiser", "ft", "feat", "featuring", "version",
        "topic", "vevo", "mv", "videoclip",
    ];

    // Versions qu'on ne veut pas si le titre cherché ne les mentionne pas.
    private static readonly string[] Unwanted =
    [
        "live", "remix", "karaoke", "cover", "reprise", "instrumental", "sped up", "slowed", "nightcore", "reverb", "8d",
        "acapella", "a cappella", "reaction", "tutorial", "tuto", "extended", "mashup", "bass boosted", "parody", "parodie",
    ];

    public static int Score(MatchQuery query, YtEntry entry)
    {
        var titleWords = Words(query.Title).Where(w => !Noise.Contains(w)).Distinct().ToList();
        var artistWords = Words(query.Artist).Distinct().ToList();
        var candidate = Words(entry.Title);
        var candidateSet = candidate.ToHashSet();

        var score = 0.0;
        if (titleWords.Count > 0) score += 45.0 * titleWords.Count(candidateSet.Contains) / titleWords.Count;

        var channel = entry.Channel ?? "";
        var topic = channel.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase);
        var channelSet = Words(channel).ToHashSet();
        if (artistWords.Count > 0 && artistWords.All(channelSet.Contains))
            score += topic ? 35 : 30;
        else if (artistWords.Count > 0 && artistWords.All(candidateSet.Contains))
            score += 25;

        score += (query.DurationSeconds, entry.DurationSeconds) switch
        {
            ({ } want, { } actual) => Math.Abs(want - actual) switch
            {
                <= 3 => 20,
                <= 8 => 15,
                <= 20 => 8,
                <= 45 => 0,
                _ => -15,
            },
            _ => 10,
        };

        var known = titleWords.Concat(artistWords).ToHashSet();
        var extras = candidate.Distinct().Count(w => !known.Contains(w) && !Noise.Contains(w));
        score -= Math.Min(10, 2 * extras);

        var asked = " " + string.Join(' ', Words(query.Title)) + " ";
        var got = " " + string.Join(' ', candidate) + " ";
        if (Unwanted.Any(u => got.Contains($" {u} ", StringComparison.Ordinal) && !asked.Contains($" {u} ", StringComparison.Ordinal)))
            score -= 45;

        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }

    /// <summary>Candidats du meilleur au moins bon (à score égal, l'ordre de YouTube).</summary>
    public static IReadOnlyList<RankedEntry> Rank(MatchQuery query, IEnumerable<YtEntry> entries) =>
        entries.DistinctBy(e => e.Id).Select((e, i) => (Ranked: new RankedEntry(e, Score(query, e)), i))
            .OrderByDescending(x => x.Ranked.Score).ThenBy(x => x.i).Select(x => x.Ranked).ToList();

    public static bool IsSure(IReadOnlyList<RankedEntry> ranked) => ranked.Count > 0 && ranked[0].Score >= SureScore;

    /// <summary>Mots en minuscules, sans accents ; tout ce qui n'est ni lettre ni chiffre sépare.</summary>
    internal static List<string> Words(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
