using System.IO.Pipelines;
using System.Text;
using SonNuage.UI.Services;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class ServerSentEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<ServerSentEvent>> ReadAllAsync(string text)
    {
        var list = new List<ServerSentEvent>();
        await foreach (var e in ServerSentEvents.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), TimeSpan.FromSeconds(5), Ct)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Events_are_split_on_blank_lines_and_comments_are_skipped()
    {
        var events = await ReadAllAsync(": ping\n\nevent: overview\ndata: {\"a\":1}\n\r\nevent: logs\ndata:[1,\ndata: 2]\n\ndata: plain\n\n");
        Assert.Equal(
            [new ServerSentEvent("overview", "{\"a\":1}"), new ServerSentEvent("logs", "[1,\n2]"), new ServerSentEvent("message", "plain")],
            events);
    }

    [Fact]
    public async Task An_unfinished_event_at_the_end_is_dropped()
    {
        Assert.Empty(await ReadAllAsync("event: overview\ndata: {}"));
    }

    [Fact]
    public async Task A_silent_stream_is_given_up()
    {
        // Rien, pas même un battement de cœur : la connexion est tenue pour perdue.
        var pipe = new Pipe();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in ServerSentEvents.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromMilliseconds(200), Ct))
            {
            }
        });
    }
}
