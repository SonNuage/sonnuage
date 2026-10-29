using Localify.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Localify.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>Services communs web/Android. L'hôte fournit IKeyValueStore, IServerEndpoint, IPlayer et IPlatformInfo.</summary>
    public static IServiceCollection AddLocalifyUi(this IServiceCollection services)
    {
        services.AddHttpClient("localify", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("localify-anonymous", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<AuthSession>();
        services.AddSingleton<ApiClient>();
        services.AddSingleton<PlaybackSettings>();
        services.AddSingleton<UiState>();
        services.AddSingleton<Localizer>();
        return services;
    }
}
