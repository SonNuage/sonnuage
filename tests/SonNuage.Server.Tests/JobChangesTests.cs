using SonNuage.Server.Jobs;
using Xunit;

namespace SonNuage.Server.Tests;

public sealed class JobChangesTests
{
    [Fact]
    public void Every_subscriber_hears_a_change_once_however_many_happened()
    {
        var changes = new JobChanges();
        using var a = changes.Subscribe();
        using var b = changes.Subscribe();

        changes.Notify();
        changes.Notify();

        foreach (var s in new[] { a, b })
        {
            Assert.True(s.Reader.TryRead(out _));
            // Les changements rapprochés se résument en un seul signal : l'abonné relit l'état une fois.
            Assert.False(s.Reader.TryRead(out _));
        }
    }

    [Fact]
    public void A_closed_subscription_hears_nothing_more()
    {
        var changes = new JobChanges();
        var s = changes.Subscribe();
        s.Dispose();

        changes.Notify();

        Assert.False(s.Reader.TryRead(out _));
        Assert.Equal(0, changes.Subscribers);
    }
}
