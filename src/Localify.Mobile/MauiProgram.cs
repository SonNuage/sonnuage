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
        builder.Services.AddSingleton<IServerEndpoint, MobileEndpoint>();
        builder.Services.AddSingleton<IPlatformInfo, MobilePlatform>();
        builder.Services.AddSingleton<AndroidPlayer>();
        builder.Services.AddSingleton<IPlayer>(sp => sp.GetRequiredService<AndroidPlayer>());
        builder.Services.AddSingleton<LibraryBrowser>();
        builder.Services.AddSingleton<QueueStore>();
        builder.Services.AddLocalifyUi();

        var app = builder.Build();
        // Les réglages de lecture sont dans les Preferences (lecture synchrone) : le service de lecture
        // peut démarrer seul (Android Auto) avant toute interface, ils doivent donc être chargés ici.
        app.Services.GetRequiredService<PlaybackSettings>().LoadAsync().GetAwaiter().GetResult();
        return app;
    }
}
