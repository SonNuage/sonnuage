using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>
/// Message « nouvelle version » du client web : la première empreinte lue sert de référence, une empreinte
/// différente ensuite signale un déploiement. Une lecture ratée (serveur qui redémarre, session pas encore prête)
/// ne doit pas couper la surveillance.
/// </summary>
public sealed class UpdateWatchTests
{
    [Fact]
    public void The_same_build_is_not_an_update()
    {
        var watch = new UpdateWatch();

        Assert.False(watch.Saw("abc"));
        Assert.False(watch.Saw("abc"));
    }

    [Fact]
    public void A_different_build_after_the_first_one_is_an_update()
    {
        var watch = new UpdateWatch();
        watch.Saw("abc");

        Assert.True(watch.Saw("def"));
    }

    [Fact]
    public void A_failed_first_read_does_not_stop_the_watch()
    {
        var watch = new UpdateWatch();

        Assert.False(watch.Saw(null));
        Assert.False(watch.Saw("abc"));
        Assert.True(watch.Saw("def"));
    }

    [Fact]
    public void A_failed_read_later_is_ignored()
    {
        var watch = new UpdateWatch();
        watch.Saw("abc");

        Assert.False(watch.Saw(null));
        Assert.True(watch.Saw("def"));
    }
}
