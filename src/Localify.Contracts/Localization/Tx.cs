using System.Text;
using System.Text.RegularExpressions;

namespace Localify.Contracts.Localization;

/// <summary>
/// Message à traduire plus tard, là où l'on connaît la langue du lecteur. Un service sans requête HTTP
/// (import YouTube, file d'attente…) produit <c>Tx.Key("youtube.alreadySubscribed")</c> ; le texte peut être stocké,
/// mêlé à un texte brut (erreur yt-dlp), puis traduit à l'affichage par <see cref="Translate"/>.
/// Forme : <c>⟦clé|nom=valeur&amp;…⟧</c>, valeurs encodées.
/// </summary>
public static partial class Tx
{
    public static string Key(string key, params (string Name, object? Value)[] args)
    {
        var sb = new StringBuilder("⟦").Append(key);
        for (var i = 0; i < args.Length; i++)
        {
            sb.Append(i == 0 ? '|' : '&').Append(args[i].Name).Append('=')
              .Append(Uri.EscapeDataString(Convert.ToString(args[i].Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        }
        return sb.Append('⟧').ToString();
    }

    /// <summary>Remplace chaque marqueur par son texte dans la langue demandée ; le reste est laissé tel quel.</summary>
    public static string Translate(this Translations t, string language, string text) =>
        !text.Contains('⟦') ? text : Marker().Replace(text, m =>
        {
            var args = m.Groups[2].Success
                ? m.Groups[2].Value.Split('&').Select(p => p.Split('=', 2))
                    .Select(p => (p[0], (object?)Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""))).ToArray()
                : [];
            // Un nombre redevient un nombre (pluriels, séparateurs de milliers).
            args = args.Select(a => a.Item2 is not string s ? a
                : long.TryParse(s, out var n) ? (a.Item1, (object?)n)
                : s.Contains('⟦') ? (a.Item1, (object?)t.Translate(language, s))
                : a).ToArray();
            return t.Get(language, m.Groups[1].Value, args);
        });

    [GeneratedRegex(@"⟦([A-Za-z0-9_.]+)(?:\|([^⟧]*))?⟧")]
    private static partial Regex Marker();
}
