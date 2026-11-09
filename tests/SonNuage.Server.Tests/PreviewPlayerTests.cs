using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Un seul extrait à la fois, qui met en pause le lecteur et s'arrête si la lecture reprend.</summary>
public sealed class PreviewPlayerTests
{
    private sealed class FakeAudio : IPreviewAudio
    {
        public List<string> Calls { get; } = [];
        public Task PlayAsync(string url) { Calls.Add("play " + url); return Task.CompletedTask; }
        public Task StopAsync() { Calls.Add("stop"); return Task.CompletedTask; }
    }

    private static Func<Task<string?>> Url(string id) => () => Task.FromResult<string?>($"preview/{id}");

    private static async Task<FakePlayer> PlayingAsync()
    {
        var player = new FakePlayer();
        await player.PlayAsync([new TrackDto(Guid.NewGuid(), "Titre", Guid.Empty, "A", Guid.Empty, "B", 1, 1, 120, "mp3", false, 192, false, false)]);
        return player;
    }

    [Fact]
    public async Task A_preview_pauses_the_player_and_plays_until_it_ends()
    {
        var player = await PlayingAsync();
        var audio = new FakeAudio();
        var preview = new PreviewPlayer(player);

        await preview.ToggleAsync("aaaaaaaaaa1", Url("aaaaaaaaaa1"), audio);
        Assert.False(player.State.IsPlaying);
        Assert.Equal(PreviewState.Loading, preview.State);
        Assert.Equal(["play preview/aaaaaaaaaa1"], audio.Calls);

        preview.OnAudio("playing");
        Assert.Equal(PreviewState.Playing, preview.StateOf("aaaaaaaaaa1"));
        Assert.Equal(PreviewState.Idle, preview.StateOf("aaaaaaaaaa2"));

        preview.OnAudio("ended");
        Assert.Equal(PreviewState.Idle, preview.State);
        Assert.Null(preview.VideoId);
    }

    [Fact]
    public async Task A_second_click_stops_and_another_video_replaces_the_first()
    {
        var audio = new FakeAudio();
        var preview = new PreviewPlayer(new FakePlayer());

        await preview.ToggleAsync("aaaaaaaaaa1", Url("aaaaaaaaaa1"), audio);
        await preview.ToggleAsync("aaaaaaaaaa2", Url("aaaaaaaaaa2"), audio);
        Assert.Equal("aaaaaaaaaa2", preview.VideoId);

        await preview.ToggleAsync("aaaaaaaaaa2", Url("aaaaaaaaaa2"), audio);
        Assert.Equal(PreviewState.Idle, preview.State);
        Assert.Equal(["play preview/aaaaaaaaaa1", "stop", "play preview/aaaaaaaaaa2", "stop"], audio.Calls);
    }

    [Fact]
    public async Task The_preview_stops_when_the_player_resumes()
    {
        var player = await PlayingAsync();
        var audio = new FakeAudio();
        var preview = new PreviewPlayer(player);
        await preview.ToggleAsync("aaaaaaaaaa1", Url("aaaaaaaaaa1"), audio);
        preview.OnAudio("playing");

        await player.TogglePlayAsync();

        Assert.Equal(PreviewState.Idle, preview.State);
        Assert.Equal("stop", audio.Calls[^1]);
    }

    [Fact]
    public async Task A_failed_preview_reports_to_whoever_started_it()
    {
        var failed = 0;
        var preview = new PreviewPlayer(new FakePlayer());
        await preview.ToggleAsync("aaaaaaaaaa1", Url("aaaaaaaaaa1"), new FakeAudio(), () => failed++);

        preview.OnAudio("error");

        Assert.Equal(1, failed);
        Assert.Equal(PreviewState.Idle, preview.State);
    }
}
