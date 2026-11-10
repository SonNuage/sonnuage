using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SonNuage.Server.Auth;

public static class AuthConstants
{
    public const string Issuer = "sonnuage";
    public const string ApiAudience = "sonnuage-api";
    public const string MediaAudience = "sonnuage-media";
    public const string MediaScheme = "Media";
    public const string MediaPolicy = "media";
    public const string AdminPolicy = "admin";
    /// <summary>Contributeur ou admin : demandes de téléchargement.</summary>
    public const string ContributorPolicy = "contributor";
    /// <summary>Contributeur ou admin, jeton d'API ou jeton média (extraits lus par un élément audio).</summary>
    public const string ContributorMediaPolicy = "contributor-media";
    public const string ApiKeyScheme = "ApiKey";
    /// <summary>Admin connecté, ou clé d'extension de navigateur : uniquement les routes d'ajout d'imports YouTube.</summary>
    public const string ExtensionPolicy = "extension";
    public const string SecurityStampClaim = "sstamp";
}

/// <summary>Clé HMAC : fournie par la config, sinon générée une fois et conservée dans le dossier de données.</summary>
public sealed class SigningKeyProvider
{
    public SymmetricSecurityKey Key { get; }

    public SigningKeyProvider(IOptions<SonNuageOptions> options)
    {
        var configured = options.Value.JwtSigningKey;
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            bytes = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        }
        else
        {
            var path = Path.Combine(options.Value.DataPath, "signing.key");
            if (File.Exists(path))
            {
                bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            }
            else
            {
                bytes = RandomNumberGenerator.GetBytes(64);
                Directory.CreateDirectory(options.Value.DataPath);
                File.WriteAllText(path, Convert.ToBase64String(bytes));
            }
        }
        Key = new SymmetricSecurityKey(bytes);
    }
}

public sealed class TokenService(
    SonNuageDbContext db,
    SigningKeyProvider keys,
    IOptions<SonNuageOptions> options,
    TimeProvider time)
{
    private static readonly JwtSecurityTokenHandler Handler = new() { MapInboundClaims = false };

    public async Task<TokenResponse> IssueAsync(User user, string? deviceName, RefreshToken? replacing, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var o = options.Value;
        var accessExpires = now.AddMinutes(o.AccessTokenMinutes);
        var mediaExpires = now.AddHours(o.MediaTokenHours);

        var access = CreateJwt(user, AuthConstants.ApiAudience, now, accessExpires);
        var media = CreateJwt(user, AuthConstants.MediaAudience, now, mediaExpires);

        var refreshValue = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refreshValue),
            DeviceName = deviceName ?? replacing?.DeviceName,
            CreatedAt = now,
            ExpiresAt = now.AddDays(o.RefreshTokenDays),
        };
        db.RefreshTokens.Add(refresh);
        // Connexion ou renouvellement de session : c'est la dernière connexion de l'utilisateur.
        user.LastSeenAt = now;
        user.LastSeenDevice = refresh.DeviceName;
        if (replacing is not null)
        {
            replacing.RevokedAt = now;
            replacing.ReplacedById = refresh.Id;
        }
        await db.SaveChangesAsync(ct);

        return new TokenResponse(access, accessExpires, refreshValue, media, mediaExpires, user.ToDto());
    }

    /// <summary>
    /// Rotation : un refresh token ne sert qu'une fois. Réutiliser un jeton déjà remplacé indique un vol :
    /// toute la lignée de l'utilisateur est révoquée.
    /// </summary>
    private static readonly TimeSpan ConcurrentRefreshGrace = TimeSpan.FromMinutes(1);

    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.Include(r => r.User).FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (stored is null) return null;

        if (stored.RevokedAt is not null)
        {
            // Renouvelé à l'instant : deux onglets du même navigateur l'ont fait en même temps. Le perdant relit
            // les jetons partagés par l'autre. Ce n'est pas un jeton volé rejoué plus tard.
            if (stored.ReplacedById is not null && now - stored.RevokedAt.Value > ConcurrentRefreshGrace)
            {
                await db.RefreshTokens.Where(r => r.UserId == stored.UserId && r.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
            }
            return null;
        }
        if (stored.ExpiresAt <= now) return null;

        return await IssueAsync(stored.User, null, stored, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var now = time.GetUtcNow();
        await db.RefreshTokens.Where(r => r.TokenHash == hash && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
    }

    private string CreateJwt(User user, string audience, DateTimeOffset now, DateTimeOffset expires)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new("role", user.Role),
            new(AuthConstants.SecurityStampClaim, user.SecurityStamp),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        var token = new JwtSecurityToken(
            AuthConstants.Issuer,
            audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256));
        return Handler.WriteToken(token);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class UserMapping
{
    public static UserDto ToDto(this User u) =>
        new(u.Id, u.Username, u.Role, u.CreatedAt, u.Language, u.RequestsPerDay, u.AutoApproveRequests, u.LastSeenAt, u.LastSeenDevice);
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? throw new InvalidOperationException("Jeton sans sub"));

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.HasClaim("role", Roles.Admin);

    /// <summary>Contributeur ou admin : voit et demande ce qui n'est pas encore dans la bibliothèque.</summary>
    public static bool CanRequest(this ClaimsPrincipal principal) => principal.IsAdmin() || principal.HasClaim("role", Roles.Contributor);
}
