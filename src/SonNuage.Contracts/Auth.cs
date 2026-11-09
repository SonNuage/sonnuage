namespace SonNuage.Contracts;

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
    /// <summary>Utilisateur qui peut aussi demander des téléchargements (voir <see cref="DownloadRequestDto"/>).</summary>
    public const string Contributor = "contributor";

    public static bool IsKnown(string? role) => role is Admin or User or Contributor;

    /// <summary>Peut créer des demandes de téléchargement.</summary>
    public static bool CanRequest(string? role) => role is Admin or Contributor;
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

// Language : langue préférée de l'interface (code d'un fichier de traduction), null = celle de l'appareil.
// RequestsPerDay, AutoApproveRequests : demandes de téléchargement d'un contributeur (quota sur 24 h, approbation sans admin).
public sealed record UserDto(Guid Id, string Username, string Role, DateTimeOffset CreatedAt, string? Language = null,
    int RequestsPerDay = DownloadRequestLimits.DefaultPerDay, bool AutoApproveRequests = false,
    DateTimeOffset? LastSeenAt = null, string? LastSeenDevice = null);

public sealed record SetLanguageRequest(string? Language);

/// <summary>
/// Accès des applis Subsonic (Symfonium, Feishin, DSub…) : nom d'utilisateur et mot de passe Subsonic, distinct de
/// celui du compte, à saisir dans l'appli avec l'adresse du serveur.
/// </summary>
public sealed record SubsonicAccessDto(bool Enabled, string Username, string? Password, DateTimeOffset? CreatedAt);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
