using System.Text.RegularExpressions;

namespace SonNuage.Server.Jam;

/// <summary>
/// Extrait le jeton de Jam d'un lien de partage Spotify (ou d'un code scanné). Un lien direct
/// (<c>open.spotify.com/socialsession/&lt;id&gt;</c>) est lu tel quel ; un lien court (<c>spotify.link/…</c>) est suivi
/// jusqu'à révéler l'identifiant de session.
/// </summary>
public static partial class SpotifyLink
{
    [GeneratedRegex(@"socialsession(?:/|%2F|:)([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionId();

    [GeneratedRegex(@"^[A-Za-z0-9]{16,}$")]
    private static partial Regex RawToken();

    /// <summary>Le jeton trouvé dans le texte (lien direct ou code), ou null.</summary>
    public static string? FromText(string link)
    {
        link = link.Trim();
        if (SessionId().Match(link) is { Success: true } m) return m.Groups[1].Value;
        if (RawToken().IsMatch(link)) return link;
        return null;
    }

    /// <summary>
    /// Le jeton de Jam d'un lien : direct tout de suite, sinon le lien court est suivi (jusqu'à 6 sauts) pour atteindre
    /// l'URL <c>socialsession</c>. Null si rien ne s'y trouve.
    /// </summary>
    public static async Task<string?> ResolveAsync(HttpClient http, string link, CancellationToken ct)
    {
        if (FromText(link) is { } direct) return direct;
        var uri = Normalize(link);
        for (var hop = 0; hop < 6 && uri is not null && IsSpotify(uri); hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.RequestMessage?.RequestUri is { } final && SessionId().Match(final.ToString()) is { Success: true } fm)
                return fm.Groups[1].Value;
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
            {
                if (SessionId().Match(loc.ToString()) is { Success: true } lm) return lm.Groups[1].Value;
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                continue;
            }
            // Dernière chance : l'identifiant est parfois dans le corps (balises Open Graph / URL complète encodée).
            var body = await ReadHeadAsync(res.Content, ct);
            return SessionId().Match(body) is { Success: true } bm ? bm.Groups[1].Value : null;
        }
        return null;
    }

    /// <summary>Taille lue au plus d'une page de lien court : l'URL cherchée est dans l'en-tête HTML.</summary>
    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// Seuls les liens de Spotify sont suivis, en https, et chaque redirection est revérifiée : le serveur ne doit
    /// jamais aller chercher une adresse choisie par l'utilisateur (réseau interne, métadonnées du cloud…).
    /// </summary>
    private static bool IsSpotify(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        (uri.Host is "spotify.link" or "spotify.app.link" or "spotify.com" || uri.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase));

    private static Uri? Normalize(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri;
        if (Uri.TryCreate("https://" + link, UriKind.Absolute, out var guessed)) return guessed;
        return null;
    }

    private static async Task<string> ReadHeadAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
