namespace SonNuage.Server.Api;

/// <summary>
/// CORS de l'appli de bureau (dépôt sonnuage-desktop, Photino.Blazor) : ses pages viennent de http://localhost
/// (WebView2, Windows) ou de app://localhost (WebKit, macOS et Linux). Son pré-chargement télécharge les pistes
/// suivantes par fetch, en cross-origin. Seuls le flux et la pochette s'ouvrent à ces deux origines, en lecture,
/// sans cookies : le jeton média est dans l'adresse.
/// </summary>
public static class DesktopCors
{
    public const string Policy = "desktop-app";

    public static readonly string[] Origins = ["http://localhost", "app://localhost"];

    public static IServiceCollection AddDesktopCors(this IServiceCollection services) =>
        services.AddCors(o => o.AddPolicy(Policy, p => p
            .WithOrigins(Origins)
            .WithMethods(HttpMethods.Get, HttpMethods.Head)
            .WithHeaders("Range")
            .WithExposedHeaders("Content-Range", "Accept-Ranges", "X-SonNuage-Quality", "X-SonNuage-Duration")
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));
}
