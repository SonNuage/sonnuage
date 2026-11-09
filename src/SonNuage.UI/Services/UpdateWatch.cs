namespace SonNuage.UI.Services;

/// <summary>
/// Empreinte du client web lue périodiquement sur le serveur : la première lue sert de référence, une autre ensuite
/// veut dire qu'une nouvelle version a été déployée. Une lecture ratée (serveur qui redémarre) est ignorée, y compris
/// la toute première : la surveillance continue.
/// </summary>
public sealed class UpdateWatch
{
    private string? _loaded;

    /// <summary>Vrai si <paramref name="build"/> diffère de la version chargée.</summary>
    public bool Saw(string? build)
    {
        if (string.IsNullOrEmpty(build)) return false;
        _loaded ??= build;
        return build != _loaded;
    }
}
