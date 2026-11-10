using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.ListenBrainz;

/// <summary>
/// Jeton ListenBrainz de chaque compte, chiffré en base comme le mot de passe Subsonic (Data Protection, clés dans
/// le dossier de données). Il ne sert qu'au serveur : l'appli ne le relit jamais.
/// </summary>
public sealed class ListenBrainzTokens(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("SonNuage.ListenBrainz.Token");

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

    /// <summary>Délie le compte : jeton, nom et envoi des écoutes effacés.</summary>
    public static Task DisconnectAsync(SonNuageDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.ListenBrainzToken, (string?)null)
            .SetProperty(u => u.ListenBrainzUserName, (string?)null)
            .SetProperty(u => u.ListenBrainzSubmitSince, (DateTimeOffset?)null), ct);

    public static async Task<ListenBrainzAccountDto> AccountAsync(SonNuageDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Where(u => u.Id == userId)
            .Select(u => new { Connected = u.ListenBrainzToken != null, u.ListenBrainzUserName, Submit = u.ListenBrainzSubmitSince != null })
            .SingleAsync(ct);
        var sent = await db.PlayEvents.CountAsync(p => p.UserId == userId && p.ListenBrainz == ListenBrainzSubmission.Sent, ct);
        return new ListenBrainzAccountDto(user.Connected, user.ListenBrainzUserName, user.Submit, sent);
    }
}
