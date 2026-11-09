using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;

namespace SonNuage.Server.Subsonic;

/// <summary>
/// Mot de passe Subsonic de chaque compte, chiffré en base (Data Protection, clés dans le dossier de données) :
/// le jeton Subsonic <c>md5(mot de passe + sel)</c> oblige à pouvoir le relire. Son empreinte sert aussi de clé
/// d'API OpenSubsonic (<c>apiKey</c>).
/// </summary>
public sealed class SubsonicPasswords(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("SonNuage.Subsonic.Password");

    public async Task<string> CreateAsync(SonNuageDbContext db, User user, DateTimeOffset now, CancellationToken ct)
    {
        var password = SubsonicCredentials.NewPassword();
        user.SubsonicPassword = _protector.Protect(password);
        user.SubsonicKeyHash = SubsonicCredentials.KeyHash(password);
        user.SubsonicPasswordCreatedAt = now;
        await db.SaveChangesAsync(ct);
        return password;
    }

    public static async Task RevokeAsync(SonNuageDbContext db, User user, CancellationToken ct)
    {
        user.SubsonicPassword = null;
        user.SubsonicKeyHash = null;
        user.SubsonicPasswordCreatedAt = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Le mot de passe en clair, ou null s'il n'y en a pas (ou si les clés de chiffrement ont été perdues).</summary>
    public string? Read(User user)
    {
        if (user.SubsonicPassword is null) return null;
        try
        {
            return _protector.Unprotect(user.SubsonicPassword);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static Task<User?> FindByKeyAsync(SonNuageDbContext db, string apiKey, CancellationToken ct)
    {
        var hash = SubsonicCredentials.KeyHash(apiKey);
        return db.Users.FirstOrDefaultAsync(u => u.SubsonicKeyHash == hash, ct);
    }
}
