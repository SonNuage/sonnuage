using Localify.UI.Services;
using Microsoft.JSInterop;

namespace Localify.Web.Services;

public sealed class BrowserStore(IJSRuntime js) : IKeyValueStore
{
    public async Task<string?> GetAsync(string key) => await js.InvokeAsync<string?>("localStorage.getItem", key);

    public async Task SetAsync(string key, string? value)
    {
        if (value is null) await js.InvokeVoidAsync("localStorage.removeItem", key);
        else await js.InvokeVoidAsync("localStorage.setItem", key, value);
    }
}

/// <summary>Sur le web, l'API est servie par la même origine que l'application.</summary>
public sealed class SameOriginEndpoint(Uri baseAddress) : IServerEndpoint
{
    public Uri? BaseAddress { get; } = baseAddress;
    public bool IsConfigurable => false;
    public Task SetAsync(Uri? address) => Task.CompletedTask;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

public sealed class BrowserPlatform : IPlatformInfo
{
    public bool HasMeteredNetworkDetection => false;
    public string Name => "web";
    public bool ServedByServer => true;
}
