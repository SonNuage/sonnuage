using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Localify.Server.Library;

public static partial class NameNormalizer
{
    /// <summary>
    /// Clé de regroupement : minuscules, sans accents, tirets typographiques unifiés,
    /// sans suffixe entre crochets/parenthèses, uniquement lettres et chiffres.
    /// « Au‐delà du délire » et « Au-Delà du Délire » donnent la même clé,
    /// « Get a Grip [Bonus Track] » aussi que « Get a Grip ».
    /// </summary>
    public static string Key(string value)
    {
        var s = StripBracketSuffix(value);
        s = RemoveDiacritics(s).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        // Un nom fait uniquement de ponctuation garde une clé non vide.
        return sb.Length > 0 ? sb.ToString() : value.Trim().ToLowerInvariant();
    }

    public static string SortName(string name)
    {
        foreach (var prefix in new[] { "The ", "Les ", "Le ", "La ", "L'" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                return name[prefix.Length..];
        }
        return name;
    }

    /// <summary>Nettoie un nom de fichier ou de dossier pour l'afficher quand aucun tag n'existe.</summary>
    public static string CleanTitle(string fileOrFolderName, params string[] prefixesToStrip)
    {
        var name = Path.GetFileNameWithoutExtension(fileOrFolderName);
        if (Path.HasExtension(fileOrFolderName) && !LibraryFiles.IsAudio(fileOrFolderName))
            name = fileOrFolderName;

        name = UnifyDashes(name).Replace('_', ' ');
        foreach (var raw in prefixesToStrip)
        {
            var prefix = UnifyDashes(raw).Replace('_', ' ');
            if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name[prefix.Length..];
        }
        name = LeadingTrackNumber().Replace(name, "");
        name = MultiSpace().Replace(name, " ").Trim(' ', '-', '.');
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(fileOrFolderName);
    }

    public static int? TrackNumberFromFileName(string fileName)
    {
        var m = LeadingNumber().Match(Path.GetFileName(fileName));
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and < 1000 ? n : null;
    }

    public static string UnifyDashes(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' ? '-' : c);
        return sb.ToString();
    }

    private static string StripBracketSuffix(string s)
    {
        var stripped = BracketSuffix().Replace(s, "").Trim();
        return stripped.Length > 0 ? stripped : s;
    }

    private static string RemoveDiacritics(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s*[\[\(][^\]\)]*[\]\)]\s*$")]
    private static partial Regex BracketSuffix();

    [GeneratedRegex(@"^\s*\d{1,3}(\s*[.\-_)]\s*|\s+)")]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"^\s*(\d{1,3})(?:[.\-_ )]|$)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
