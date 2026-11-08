using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Clé de traduction du nom d'un rôle.</summary>
public static class RoleLabels
{
    /// <param name="section">users : nom seul, avec majuscule ; settings : au fil d'une phrase.</param>
    public static string Key(string? role, string section = "users") => role switch
    {
        Roles.Admin => $"{section}.roleAdmin",
        Roles.Contributor => $"{section}.roleContributor",
        _ => $"{section}.roleUser",
    };
}
