using Microsoft.AspNetCore.Components;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Retour du téléphone : il referme le menu, la fenêtre ou le plein écran ouvert au lieu de changer de page.</summary>
public class BackNavigationTests
{
    [Fact]
    public async Task Back_closes_what_is_open_and_stays_on_the_page()
    {
        var nav = new TestNav();
        var open = true;
        using var _ = nav.CloseOnBack(() => open, () => open = false);

        Assert.False(await nav.ChangeAsync("settings", intercepted: false));
        Assert.False(open);
    }

    [Fact]
    public async Task Back_with_nothing_open_changes_page()
    {
        var nav = new TestNav();
        using var _ = nav.CloseOnBack(() => false, () => throw new InvalidOperationException());

        Assert.True(await nav.ChangeAsync("settings", intercepted: false));
    }

    [Fact]
    public async Task A_link_clicked_inside_what_is_open_still_navigates()
    {
        var nav = new TestNav();
        var open = true;
        using var _ = nav.CloseOnBack(() => open, () => open = false);

        Assert.True(await nav.ChangeAsync("album/1", intercepted: true));
    }

    [Fact]
    public async Task Once_disposed_it_no_longer_holds_back()
    {
        var nav = new TestNav();
        nav.CloseOnBack(() => true, () => { }).Dispose();

        Assert.True(await nav.ChangeAsync("settings", intercepted: false));
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav() => Initialize("https://app.example/", "https://app.example/");

        public ValueTask<bool> ChangeAsync(string relative, bool intercepted) =>
            NotifyLocationChangingAsync(ToAbsoluteUri(relative).ToString(), null, intercepted);

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();

        protected override void SetNavigationLockState(bool value) { }
    }
}
