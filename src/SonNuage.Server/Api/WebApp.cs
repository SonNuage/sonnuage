namespace SonNuage.Server.Api;

/// <summary>
/// Client web (Blazor) servi par le serveur. Les fichiers sans empreinte dans leur nom (feuille de style, scripts de
/// démarrage) reçoivent la version du client en paramètre : un proxy qui impose sa propre durée de cache (Cloudflare,
/// 4 h) ne peut plus servir l'ancienne interface après un déploiement.
/// </summary>
public static class WebApp
{
    private const string Placeholder = "__BUILD__";
    private static string? _build;
    private static string? _index;

    /// <summary>
    /// Empreinte du client : dotnet.js embarque la liste des assemblies avec leur empreinte, il change donc dès qu'une
    /// ligne de l'interface change. Calculée une fois (les fichiers ne bougent pas pendant la vie du processus).
    /// </summary>
    public static string? Build(IWebHostEnvironment env)
    {
        if (_build is null)
        {
            var file = env.WebRootFileProvider.GetFileInfo("_framework/dotnet.js");
            if (!file.Exists) _build = "";
            else
            {
                using var stream = file.CreateReadStream();
                _build = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))[..16].ToLowerInvariant();
            }
        }
        return _build.Length == 0 ? null : _build;
    }

    /// <summary>index.html avec la version du client. Jamais mis en cache (c'est lui qui pointe vers la bonne version).</summary>
    public static IResult Index(HttpContext context, IWebHostEnvironment env)
    {
        if (_index is null)
        {
            var file = env.WebRootFileProvider.GetFileInfo("index.html");
            if (!file.Exists) return Results.NotFound();
            using var reader = new StreamReader(file.CreateReadStream());
            _index = reader.ReadToEnd().Replace(Placeholder, Build(env) ?? "dev", StringComparison.Ordinal);
        }
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(_index, "text/html; charset=utf-8");
    }
}
