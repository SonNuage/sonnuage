using Microsoft.JSInterop;

namespace SonNuage.UI.Services;

public enum PreviewState { Idle, Loading, Playing }

/// <summary>L'audio d'un extrait : une balise audio à part, hors de la file du lecteur.</summary>
public interface IPreviewAudio
{
    Task PlayAsync(string url);
    Task StopAsync();
}

/// <summary>
/// Extraits YouTube écoutés avant de demander un morceau. Un seul à la fois : il met en pause le lecteur, et s'arrête
/// si la lecture reprend. Le serveur prépare l'extrait à la première écoute (quelques secondes) : état « Loading »
/// jusqu'à ce que le son parte.
/// </summary>
public sealed class PreviewPlayer
{
    private readonly IPlayer _player;
    private IPreviewAudio? _audio;
    private Action? _onFailed;

    public PreviewPlayer(IPlayer player)
    {
        _player = player;
        _player.StateChanged += () =>
        {
            if (_player.State.IsPlaying && State != PreviewState.Idle) _ = StopAsync();
        };
    }

    public string? VideoId { get; private set; }
    public PreviewState State { get; private set; }
    public event Action? Changed;

    public PreviewState StateOf(string videoId) => VideoId == videoId ? State : PreviewState.Idle;

    /// <summary>Lance l'extrait de <paramref name="videoId"/>, ou l'arrête s'il est déjà lancé.</summary>
    /// <param name="onFailed">Prévenu si l'extrait ne peut pas être lu (vidéo bloquée, yt-dlp en échec…).</param>
    public async Task ToggleAsync(string videoId, Func<Task<string?>> url, IPreviewAudio audio, Action? onFailed = null)
    {
        var again = VideoId == videoId;
        await StopAsync();
        if (again) return;

        VideoId = videoId;
        _audio = audio;
        _onFailed = onFailed;
        Set(PreviewState.Loading);
        if (_player.State.IsPlaying) await _player.TogglePlayAsync();

        var address = await url();
        if (VideoId != videoId) return;
        if (address is null)
        {
            Fail();
            return;
        }
        await audio.PlayAsync(address);
    }

    public async Task StopAsync()
    {
        var audio = _audio;
        var active = State != PreviewState.Idle;
        Reset();
        if (active && audio is not null) await audio.StopAsync();
    }

    /// <summary>État rapporté par la balise audio : « playing », « ended » ou « error ».</summary>
    public void OnAudio(string state)
    {
        if (VideoId is null) return;
        switch (state)
        {
            case "playing":
                Set(PreviewState.Playing);
                break;
            case "ended":
                Reset();
                break;
            case "error":
                Fail();
                break;
        }
    }

    private void Fail()
    {
        var onFailed = _onFailed;
        Reset();
        onFailed?.Invoke();
    }

    private void Reset()
    {
        VideoId = null;
        _audio = null;
        _onFailed = null;
        Set(PreviewState.Idle);
    }

    private void Set(PreviewState state)
    {
        State = state;
        Changed?.Invoke();
    }
}

/// <summary>Extrait lu par ui.js (window.sonnuage.preview), qui rapporte son état au <see cref="PreviewPlayer"/>.</summary>
public sealed class JsPreviewAudio(IJSRuntime js, PreviewPlayer owner) : IPreviewAudio, IDisposable
{
    private DotNetObjectReference<JsPreviewAudio>? _ref;

    public async Task PlayAsync(string url)
    {
        _ref ??= DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("sonnuage.preview.play", url, _ref);
    }

    public async Task StopAsync()
    {
        try
        {
            await js.InvokeVoidAsync("sonnuage.preview.stop");
        }
        catch (JSDisconnectedException)
        {
        }
    }

    [JSInvokable]
    public void OnPreviewAudio(string state) => owner.OnAudio(state);

    public void Dispose() => _ref?.Dispose();
}
