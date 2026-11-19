using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Jam;

/// <summary>
/// Jetons Spotify de chaque compte, chiffrés en base comme le jeton ListenBrainz (Data Protection, clés dans le dossier
/// de données). Ils ne servent qu'au serveur, pour suivre un Jam et rafraîchir l'accès ; l'appli ne les relit jamais.
/// </summary>
public sealed class SpotifyTokens(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("SonNuage.Spotify.Token");

    public string Protect(string token) => _protector.Protect(token);

    /// <summary>Le jeton en clair, ou null s'il n'y en a pas (ou si les clés de chiffrement ont été perdues).</summary>
    public string? Read(string? stored)
    {
        if (stored is null) return null;
        try
        {
            return _protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Enregistre (ou remplace) les jetons d'un compte, chiffrés.</summary>
    public Task SaveAsync(SonNuageDbContext db, Guid userId, string accessToken, string? refreshToken, DateTimeOffset expiresAt,
        string? displayName, CancellationToken ct)
    {
        var access = Protect(accessToken);
        var refresh = refreshToken is null ? null : Protect(refreshToken);
        return db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.SpotifyAccessToken, access)
            .SetProperty(u => u.SpotifyRefreshToken, refresh)
            .SetProperty(u => u.SpotifyExpiresAt, expiresAt)
            .SetProperty(u => u.SpotifyDisplayName, displayName), ct);
    }

    /// <summary>Délie le compte du service : jetons, expiration et nom effacés.</summary>
    public static Task DisconnectAsync(SonNuageDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.SpotifyAccessToken, (string?)null)
            .SetProperty(u => u.SpotifyRefreshToken, (string?)null)
            .SetProperty(u => u.SpotifyExpiresAt, (DateTimeOffset?)null)
            .SetProperty(u => u.SpotifyDisplayName, (string?)null), ct);

    public static async Task<MusicServiceAccountDto> AccountAsync(SonNuageDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Where(u => u.Id == userId)
            .Select(u => new { Connected = u.SpotifyAccessToken != null, u.SpotifyDisplayName })
            .SingleAsync(ct);
        return new MusicServiceAccountDto(Contracts.Jam.Spotify, user.Connected, user.SpotifyDisplayName);
    }
}
