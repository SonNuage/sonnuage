namespace Localify.Contracts;

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public sealed record LoginRequest(string Username, string Password, string? DeviceName);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>
/// <see cref="MediaToken"/> s'ajoute en query (<c>?t=</c>) aux URL de flux et de pochettes,
/// pour les lecteurs qui ne savent pas envoyer d'en-tête Authorization (balise audio, img).
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    string MediaToken,
    DateTimeOffset MediaTokenExpiresAt,
    UserDto User);

public sealed record UserDto(Guid Id, string Username, string Role, DateTimeOffset CreatedAt);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
