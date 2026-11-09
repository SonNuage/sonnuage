using System.Globalization;
using System.Text.RegularExpressions;
using SonNuage.Contracts;

namespace SonNuage.Server.Lyrics;

/// <summary>
/// Paroles au format LRC (« [01:02.50]texte »), ou texte brut quand aucune ligne n'est datée.
/// Plusieurs balises de temps par ligne, métadonnées ignorées, décalage [offset:±ms] appliqué.
/// </summary>
public static partial class LrcParser
{
    public static LyricsDto Parse(string text, string? source)
    {
        var rows = text.ReplaceLineEndings("\n").Split('\n');
        var offset = rows.Select(r => OffsetTag().Match(r.Trim())).FirstOrDefault(m => m.Success) is { } m
            && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) ? ms / 1000.0 : 0;

        var timed = rows.SelectMany(r => Timed(r.Trim(), offset)).OrderBy(l => l.StartSeconds).ToList();
        return timed.Count > 0
            ? new LyricsDto(true, timed, source)
            : new LyricsDto(false, Plain(rows), source);
    }

    private static IEnumerable<LyricLineDto> Timed(string row, double offset)
    {
        var match = TimedRow().Match(row);
        if (!match.Success) yield break;
        var text = WordTag().Replace(match.Groups["text"].Value, "").Trim();
        foreach (Capture tag in match.Groups["tag"].Captures)
            yield return new LyricLineDto(Math.Max(0, Math.Round(Seconds(tag.Value) - offset, 3)), text);
    }

    // « 01:02.5 » : minutes, secondes, fraction (dixièmes, centièmes ou millièmes).
    private static double Seconds(string tag)
    {
        var parts = tag.Split(':', 2);
        var seconds = parts[1].Replace(':', '.');
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + double.Parse(seconds, CultureInfo.InvariantCulture);
    }

    // Texte brut : sans métadonnées, sans lignes vides en tête ni en fin, une seule ligne vide entre deux strophes.
    private static List<LyricLineDto> Plain(IEnumerable<string> rows)
    {
        var lines = new List<LyricLineDto>();
        foreach (var row in rows.Select(r => r.Trim()).Where(r => !MetadataTag().IsMatch(r)))
        {
            if (row.Length == 0 && (lines.Count == 0 || lines[^1].Text.Length == 0)) continue;
            lines.Add(new LyricLineDto(null, row));
        }
        if (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    [GeneratedRegex(@"^(?:\[(?<tag>\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?)\])+(?<text>.*)$")]
    private static partial Regex TimedRow();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();

    [GeneratedRegex(@"^\[[A-Za-z#]+:[^\]]*\]$")]
    private static partial Regex MetadataTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();
}
