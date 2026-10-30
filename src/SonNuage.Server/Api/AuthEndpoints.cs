using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest req, UserService users, TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.ValidateAsync(req.Username ?? "", req.Password ?? "", ct);
            if (user is null) return Results.Problem(Tx.Key("errors.auth.invalid"), statusCode: StatusCodes.Status401Unauthorized);
            return Results.Ok(await tokens.IssueAsync(user, Truncate(req.DeviceName, 100), null, ct));
        }).RequireRateLimiting(LoginRateLimit).AllowAnonymous();

        g.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            var result = await tokens.RefreshAsync(req.RefreshToken ?? "", ct);
            return result is null
                ? Results.Problem(Tx.Key("errors.sessionExpired"), statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(result);
        }).RequireRateLimiting(LoginRateLimit).AllowAnonymous();

        g.MapPost("/logout", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(req.RefreshToken ?? "", ct);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            return user is null ? Results.Unauthorized() : Results.Ok(user.ToDto());
        }).RequireAuthorization();

        g.MapPut("/language", async (SetLanguageRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            if (user is null) return Results.Unauthorized();
            var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language.Trim();
            if (language is not null && !Translations.Instance.IsAvailable(language))
                return Results.Problem(Tx.Key("errors.unknownLanguage", ("language", language)), statusCode: StatusCodes.Status400BadRequest);
            user.Language = language;
            await db.SaveChangesAsync(ct);
            return Results.Ok(user.ToDto());
        }).RequireAuthorization();

        g.MapPost("/password", async (ChangePasswordRequest req, HttpContext http, SonNuageDbContext db, UserService users, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            if (user is null) return Results.Unauthorized();
            if (await users.ValidateAsync(user.Username, req.CurrentPassword, ct) is null)
                return Results.Problem(Tx.Key("errors.auth.wrongPassword"), statusCode: StatusCodes.Status400BadRequest);
            if (req.NewPassword.Length < UserService.MinPasswordLength)
                return Results.Problem(Tx.Key("errors.passwordMin", ("min", UserService.MinPasswordLength)), statusCode: StatusCodes.Status400BadRequest);
            await users.SetPasswordAsync(user, req.NewPassword, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
