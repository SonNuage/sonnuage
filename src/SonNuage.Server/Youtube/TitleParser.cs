using System.Text.RegularExpressions;
using SonNuage.Server.Library;

namespace SonNuage.Server.Youtube;

public sealed record ParsedTitle(string? Artist, string Title, string? Featuring);

/// <summary>
/// Devine artiste et titre à partir d'une vidéo YouTube ordinaire : « Artiste - Titre (Official Video) »,
/// chaîne « Artiste - Topic » ou « ArtisteVEVO »… Sert de point de départ à la recherche MusicBrainz.
/// </summary>
public static partial class TitleParser
{
    public static ParsedTitle Parse(string videoTitle, string? channel)
    {
        var title = NameNormalizer.UnifyDashes(videoTitle).Replace('＂', '"');
        title = Noise().Replace(title, " ");
        title = Spaces().Replace(title, " ").Trim(' ', '-', '|', '·');

        string? featuring = null;
        var feat = Featuring().Match(title);
        if (feat.Success)
        {
            featuring = feat.Groups["who"].Value.Trim();
            title = (title[..feat.Index] + title[(feat.Index + feat.Length)..]).Trim();
        }

        string? artist = null;
        // « Artiste - Titre », « Artiste | Titre », « Artiste : Titre », « Artiste: Titre »
        var split = Separator().Match(title);
        if (split.Success)
        {
            artist = title[..split.Index].Trim();
            title = title[(split.Index + split.Length)..].Trim();
        }
        else
        {
            // « Artiste "Titre" »
            var quoted = Quoted().Match(title);
            if (quoted.Success && quoted.Groups["artist"].Value.Trim().Length > 0)
            {
                artist = quoted.Groups["artist"].Value.Trim();
                title = quoted.Groups["title"].Value.Trim();
            }
        }
        title = title.Trim('"', '\'', '«', '»', '“', '”', ' ');

        var fromChannel = ChannelArtist(channel);
        artist ??= fromChannel;
        return new ParsedTitle(string.IsNullOrWhiteSpace(artist) ? null : artist, title.Length > 0 ? title : videoTitle.Trim(), featuring);
    }

    /// <summary>« Daft Punk - Topic » → Daft Punk ; « DaftPunkVEVO » → DaftPunk ; « Daft Punk Official » → Daft Punk.</summary>
    public static string? ChannelArtist(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var c = ChannelSuffix().Replace(channel.Trim(), "").Trim();
        return c.Length > 0 ? c : null;
    }

    /// <summary>Similarité de 0 à 1 entre deux noms (bigrammes de Dice sur les clés normalisées).</summary>
    public static double Similarity(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var x = NameNormalizer.Key(a);
        var y = NameNormalizer.Key(b);
        if (x == y) return 1;
        if (x.Length < 2 || y.Length < 2) return 0;
        var bigrams = new Dictionary<int, int>();
        for (var i = 0; i < x.Length - 1; i++)
        {
            var k = x[i] << 16 | x[i + 1];
            bigrams[k] = bigrams.GetValueOrDefault(k) + 1;
        }
        var common = 0;
        for (var i = 0; i < y.Length - 1; i++)
        {
            var k = y[i] << 16 | y[i + 1];
            if (bigrams.TryGetValue(k, out var n) && n > 0)
            {
                bigrams[k] = n - 1;
                common++;
            }
        }
        return 2.0 * common / (x.Length - 1 + y.Length - 1);
    }

    [GeneratedRegex(@"[\(\[](?:[^\)\]]*\b(?:official|officiel(?:le)?|video|vid[ée]o|clip|audio|lyrics?|paroles|visuali[sz]er|hd|hq|4k|remaster(?:ed)?(?: \d{4})?|explicit|music video|live session|color coded)\b[^\)\]]*)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s*[\(\[]?\b(?:feat\.?|ft\.?|featuring)\s+(?<who>[^\)\]\-|]+)[\)\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s+[-|:~]\s+|(?<=\w):\s+")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(?<artist>[^""«“]+?)\s*[""«“](?<title>[^""»”]+)[""»”]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?:\s*-\s*Topic|VEVO|\s+Official(?:\s+Channel)?|\s+Officiel|\s+Music)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
