using Localify.Mobile.Playback;
using Localify.Mobile.Services;
using Localify.UI;
using Localify.UI.Services;

namespace Localify.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton<IKeyValueStore, MobileStore>();
        builder.Services.AddSingleton<IServerEndpoint, AccountEndpoint>();
        builder.Services.AddSingleton<IPlatformInfo, MobilePlatform>();
        builder.Services.AddSingleton<AndroidPlayer>();
        builder.Services.AddSingleton<IPlayer>(sp => sp.GetRequiredService<AndroidPlayer>());
        builder.Services.AddSingleton<LibraryBrowser>();
        builder.Services.AddSingleton<QueueStore>();
        builder.Services.AddLocalifyUi(multiAccount: true);

        var app = builder.Build();
        // Le service de lecture peut démarrer seul (Android Auto) avant toute interface : comptes (migration comprise)
        // et réglages de lecture du compte actif sont chargés ici. Hors du thread principal : SecureStorage y reviendrait
        // et l'attente synchrone le bloquerait.
        var accounts = app.Services.GetRequiredService<AccountStore>();
        var settings = app.Services.GetRequiredService<PlaybackSettings>();
        Task.Run(async () =>
        {
            await accounts.LoadAsync();
            await settings.LoadAsync();
        }).GetAwaiter().GetResult();
        return app;
    }

    /// <summary>Application minimale qui affiche le rapport quand le démarrage normal a échoué.</summary>
    public static MauiApp CreateCrashApp(string report)
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<CrashApp>();
        builder.Services.AddSingleton(new CrashReport(report));
        return builder.Build();
    }
}
