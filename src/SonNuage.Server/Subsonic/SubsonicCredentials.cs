using System.Security.Cryptography;
using System.Text;

namespace SonNuage.Server.Subsonic;

/// <summary>
/// Mot de passe Subsonic d'un compte : distinct du mot de passe SonNuage (stocké haché, donc inutilisable pour le
/// jeton <c>md5(mot de passe + sel)</c> des clients Subsonic). Généré dans les Réglages, révocable.
/// </summary>
public static class SubsonicCredentials
{
    // Sans 0/O, 1/l/I : à recopier sur un téléphone ou une télé sans se tromper.
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewPassword() => RandomNumberGenerator.GetString(Alphabet, 20);

    /// <summary>Jeton Subsonic : <c>t = md5(mot de passe + s)</c>, en hexadécimal.</summary>
    public static bool TokenMatches(string password, string salt, string token)
    {
        var expected = MD5.HashData(Encoding.UTF8.GetBytes(password + salt));
        byte[] given;
        try
        {
            given = Convert.FromHexString(token);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>Paramètre <c>p</c> : en clair, ou <c>enc:</c> suivi de l'UTF-8 en hexadécimal.</summary>
    public static string? DecodePassword(string p)
    {
        if (!p.StartsWith("enc:", StringComparison.Ordinal)) return p;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(p[4..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool PasswordMatches(string expected, string given) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    /// <summary>Empreinte de la clé d'API OpenSubsonic (<c>apiKey</c>), pour retrouver le compte sans le nom.</summary>
    public static string KeyHash(string password) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("subsonic:" + password)));
}
