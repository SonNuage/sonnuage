using System.Text.Json;
using SonNuage.UI.Services;
using Microsoft.JSInterop;

namespace SonNuage.Web.Services;

/// <summary>Relie les onglets du navigateur par BroadcastChannel (wwwroot/js/link.js).</summary>
public sealed class BroadcastPlaybackLink(IJSRuntime js) : IPlaybackLink, IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<BroadcastPlaybackLink>? _self;

    public string DeviceId { get; } = Guid.NewGuid().ToString("N")[..12];

    public Func<PlaybackMessage, Task>? OnMessage { get; set; }

    public async Task StartAsync()
    {
        if (_module is not null) return;
        _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/link.js");
        _self = DotNetObjectReference.Create(this);
        await _module.InvokeAsync<bool>("start", _self, DeviceId);
    }

    public async Task SendAsync(PlaybackMessage message)
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("send", JsonSerializer.Serialize(message, JsonDefaults.Options));
    }

    [JSInvokable]
    public Task OnLinkMessage(string json)
    {
        PlaybackMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<PlaybackMessage>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }
        if (message is null || message.From == DeviceId || message.To is { } to && to != DeviceId) return Task.CompletedTask;
        return OnMessage?.Invoke(message) ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
        _self?.Dispose();
    }
}
