namespace SonNuage.Server.Library;

/// <summary>
/// Dossier des envois (<c>uploads/</c>) : le seul où SonNuage écrit ou supprime des fichiers de musique. La
/// bibliothèque principale (<c>music/</c>) est en lecture seule. Les chemins sont normalisés avant la comparaison
/// (« uploads/../music » n'est pas dans uploads/).
/// </summary>
public static class UploadArea
{
    public static string Root(SonNuageOptions options) => Path.GetFullPath(options.UploadPath);

    public static bool Contains(SonNuageOptions options, string path) => LibraryScanner.IsUnder(Path.GetFullPath(path), Root(options));

    /// <summary>Supprime un fichier d'uploads/. Tout chemin hors du dossier est refusé (false) et rien n'est touché.</summary>
    /// <exception cref="IOException">Suppression impossible (fichier ouvert…).</exception>
    public static bool TryDelete(SonNuageOptions options, string path)
    {
        var full = Path.GetFullPath(path);
        if (!Contains(options, full)) return false;
        if (File.Exists(full)) File.Delete(full);
        return true;
    }
}
