using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Localify.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Localify.Server.Auth;

/// <summary>
/// « Authorization: ApiKey &lt;clé&gt; » : clé d'extension de navigateur. Le principal obtenu ne porte pas le rôle admin,
/// il ne satisfait que <see cref="AuthConstants.ExtensionPolicy"/> (quelques routes d'import YouTube).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Prefix = "ApiKey ";
    public const string KindClaim = "api_key_kind";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var secret = header[Prefix.Length..].Trim();
        if (secret.Length is 0 or > 200) return AuthenticateResult.Fail("Clé invalide");

        var db = Context.RequestServices.GetRequiredService<LocalifyDbContext>();
        var hash = Hash(secret);
        // La clé ne vaut que tant que l'admin qui l'a créée l'est encore.
        var key = await db.ApiKeys.Include(k => k.CreatedBy)
            .FirstOrDefaultAsync(k => k.KeyHash == hash && k.CreatedBy.Role == Localify.Contracts.Roles.Admin, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Clé inconnue ou révoquée");

        var now = time.GetUtcNow();
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("sub", key.CreatedById.ToString()),
            new Claim("api_key_id", key.Id.ToString()),
            new Claim(KindClaim, key.Kind.ToString()),
        ], Scheme.Name, "sub", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string NewSecret() => "lfy_ext_" + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
