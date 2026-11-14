using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

internal sealed class MemoryStore : IKeyValueStore
{
    public Dictionary<string, string> Data { get; } = [];

    public Task<string?> GetAsync(string key) => Task.FromResult(Data.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value)
    {
        if (value is null) Data.Remove(key);
        else Data[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class FixedEndpoint(Uri? address) : IServerEndpoint
{
    public Uri? BaseAddress => address;
    public bool IsConfigurable => false;
    public Task SetAsync(Uri? value) => Task.CompletedTask;
    public event Action? Changed { add { } remove { } }
}

internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(20, cancellationToken);
        return respond(request);
    }

    public IHttpClientFactory Factory() => new HandlerFactory(this);
}

internal sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
