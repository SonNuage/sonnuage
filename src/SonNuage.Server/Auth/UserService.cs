using SonNuage.Contracts.Localization;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Auth;

public sealed class UserService(SonNuageDbContext db, IPasswordHasher<User> hasher, IMemoryCache cache)
{
    public const int MinPasswordLength = 8;

    public static string StampCacheKey(Guid userId) => $"sstamp:{userId}";

    /// <summary>Invalide tous les jetons d'accès du compte, sans attendre l'expiration du cache des tampons.</summary>
    public void RotateSecurityStamp(User user)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        cache.Remove(StampCacheKey(user.Id));
    }

    public static string Normalize(string username) => username.Trim().ToUpperInvariant();

    public async Task<User?> ValidateAsync(string username, string password, CancellationToken ct)
    {
        var normalized = Normalize(username);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        if (user is null)
        {
            // Même coût qu'un vrai contrôle, pour ne pas révéler l'existence du compte par le temps de réponse.
            hasher.HashPassword(new User { Username = "", NormalizedUsername = "", Role = Roles.User }, password);
            return null;
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed) return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
            await db.SaveChangesAsync(ct);
        }
        return user;
    }

    public async Task<(User? User, string? Error)> CreateAsync(string username, string password, string role, CancellationToken ct)
    {
        username = username.Trim();
        if (username.Length is < 2 or > 64) return (null, Tx.Key("errors.users.usernameLength"));
        if (password.Length < MinPasswordLength) return (null, Tx.Key("errors.passwordMin", ("min", MinPasswordLength)));
        if (!Roles.IsKnown(role)) return (null, Tx.Key("errors.unknownRole"));
        var normalized = Normalize(username);
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct)) return (null, Tx.Key("errors.users.exists"));

        var user = new User { Username = username, NormalizedUsername = normalized, Role = role };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return (user, null);
    }

    public async Task SetPasswordAsync(User user, string password, CancellationToken ct)
    {
        user.PasswordHash = hasher.HashPassword(user, password);
        RotateSecurityStamp(user);
        await db.RefreshTokens.Where(r => r.UserId == user.Id && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Crée l'admin initial au premier démarrage.</summary>
public sealed class InitialAdminSeeder(UserService users, SonNuageDbContext db, IOptions<SonNuageOptions> options, ILogger<InitialAdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct)) return;
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.InitialAdminUser) || string.IsNullOrWhiteSpace(o.InitialAdminPassword))
        {
            logger.LogWarning(
                "Aucun utilisateur en base. Définir SonNuage__InitialAdminUser et SonNuage__InitialAdminPassword pour créer le premier administrateur.");
            return;
        }
        var (_, error) = await users.CreateAsync(o.InitialAdminUser, o.InitialAdminPassword, Roles.Admin, ct);
        if (error is not null) logger.LogError("Création de l'administrateur initial impossible : {Error}", error);
        else logger.LogInformation("Administrateur initial « {User} » créé", o.InitialAdminUser);
    }
}
