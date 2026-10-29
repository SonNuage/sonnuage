using Localify.UI;
using Localify.UI.Services;
using Localify.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<IServerEndpoint>(new SameOriginEndpoint(new Uri(builder.HostEnvironment.BaseAddress)));
builder.Services.AddSingleton<IKeyValueStore, BrowserStore>();
builder.Services.AddSingleton<IPlatformInfo, BrowserPlatform>();
builder.Services.AddSingleton<WebPlayer>();
builder.Services.AddSingleton<IPlayer>(sp => sp.GetRequiredService<WebPlayer>());
builder.Services.AddLocalifyUi();

var host = builder.Build();
await host.Services.GetRequiredService<PlaybackSettings>().LoadAsync();
await host.RunAsync();
