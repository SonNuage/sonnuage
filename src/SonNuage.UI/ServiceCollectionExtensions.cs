using Microsoft.Extensions.DependencyInjection.Extensions;
using SonNuage.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>Services communs web/Android. L'hôte fournit IKeyValueStore, IServerEndpoint, IPlayer et IPlatformInfo.</summary>
    /// <param name="multiAccount">
    /// Android : plusieurs comptes (serveur + utilisateur), dont un actif. Le web reste mono-compte (même origine).
    /// </param>
    public static IServiceCollection AddSonNuageUi(this IServiceCollection services, bool multiAccount = false)
    {
        services.AddSingleton(sp => new AccountStore(sp.GetRequiredService<IKeyValueStore>(), multiAccount));
        services.AddHttpClient("sonnuage", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("sonnuage-anonymous", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<AuthSession>();
        services.AddSingleton<ApiClient>();
        services.AddSingleton<IPlaybackPreferencesApi>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<PlaybackSettings>();
        services.AddSingleton<UiState>();
        services.AddSingleton<PreviewPlayer>();
        services.AddSingleton<Localizer>();
        services.AddSingleton<ThemeSettings>();
        // L'appli hôte peut fournir la sienne (partagée avec son journal de plantage).
        services.TryAddSingleton<Breadcrumbs>(_ => new Breadcrumbs());
        return services;
    }
}
