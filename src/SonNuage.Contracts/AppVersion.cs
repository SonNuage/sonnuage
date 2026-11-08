using System.Reflection;

namespace SonNuage.Contracts;

/// <summary>Version publiée d'un composant (serveur, interface, application), telle qu'affichée à l'utilisateur.</summary>
public static class AppVersion
{
    /// <summary>Version de publication de l'assembly (X.Y.Z ou préversion), sans les métadonnées de build (« +sha »).</summary>
    public static string Of(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } v
            ? Clean(v)
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Clean(string informational)
    {
        var v = informational.Trim();
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
