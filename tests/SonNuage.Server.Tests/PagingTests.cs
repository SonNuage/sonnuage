using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>
/// Listes chargées par pages (« Afficher plus ») : un même élément ne doit jamais y figurer deux fois, sinon Blazor
/// plante sur la clé en double (rapport de plantage Android 1.11.0, bibliothèque → artistes).
/// </summary>
public sealed class PagingTests
{
    private sealed record Item(int Id, string Name);

    [Fact]
    public void A_page_is_appended_in_order()
    {
        var list = new List<Item> { new(1, "a") };

        Paging.AppendNew(list, [new(2, "b"), new(3, "c")], i => i.Id);

        Assert.Equal([1, 2, 3], list.Select(i => i.Id));
    }

    [Fact]
    public void Items_already_in_the_list_are_skipped()
    {
        var list = new List<Item> { new(1, "a"), new(2, "b") };

        // La même première page reçue deux fois (deux chargements en même temps).
        Paging.AppendNew(list, [new(1, "a"), new(2, "b"), new(3, "c")], i => i.Id);

        Assert.Equal([1, 2, 3], list.Select(i => i.Id));
    }

    [Fact]
    public void Duplicates_inside_a_page_are_skipped()
    {
        var list = new List<Item>();

        Paging.AppendNew(list, [new(1, "a"), new(1, "a"), new(2, "b")], i => i.Id);

        Assert.Equal([1, 2], list.Select(i => i.Id));
    }

    [Fact]
    public void A_load_started_before_a_reset_is_stale()
    {
        var loads = new LoadGeneration();
        var first = loads.Start();

        var second = loads.Start();

        Assert.False(loads.IsCurrent(first));
        Assert.True(loads.IsCurrent(second));
    }
}
