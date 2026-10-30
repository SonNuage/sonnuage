using SonNuage.UI;
using SonNuage.UI.Services;
using SonNuage.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<IServerEndpoint>(new SameOriginEndpoint(new Uri(builder.HostEnvironment.BaseAddress)));
builder.Services.AddSingleton<IKeyValueStore, BrowserStore>();
builder.Services.AddSingleton<IPlatformInfo, BrowserPlatform>();
builder.Services.AddSingleton<WebPlayer>();
builder.Services.AddSingleton<BroadcastPlaybackLink>();
// Un seul onglet joue ; les autres le pilotent (voir LinkedPlayer).
builder.Services.AddSingleton<IPlayer>(sp => new LinkedPlayer(
    sp.GetRequiredService<WebPlayer>(), sp.GetRequiredService<WebPlayer>(), sp.GetRequiredService<BroadcastPlaybackLink>()));
builder.Services.AddSonNuageUi();

var host = builder.Build();
await host.Services.GetRequiredService<PlaybackSettings>().LoadAsync();
await host.RunAsync();
