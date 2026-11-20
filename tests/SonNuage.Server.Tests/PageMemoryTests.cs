using Microsoft.JSInterop;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Retour arrière : une page retrouve ce qu'elle affichait ; ouverte autrement, elle repart de zéro.</summary>
public class PageMemoryTests
{
    private sealed record State(string Query);

    [Fact]
    public async Task Coming_back_finds_what_the_page_showed()
    {
        var memory = new PageMemory();
        memory.Keep("requests", new State("daft punk"));

        Assert.Equal(new State("daft punk"), await memory.RecallAsync<State>(new Js(cameBack: true), "requests"));
    }

    [Fact]
    public async Task Opening_the_page_from_a_link_starts_afresh()
    {
        var memory = new PageMemory();
        memory.Keep("requests", new State("daft punk"));

        Assert.Null(await memory.RecallAsync<State>(new Js(cameBack: false), "requests"));
        Assert.Null(await memory.RecallAsync<State>(new Js(cameBack: true), "library/albums"));
    }

    [Fact]
    public async Task A_host_without_the_script_starts_afresh()
    {
        var memory = new PageMemory();
        memory.Keep("requests", new State("daft punk"));

        Assert.Null(await memory.RecallAsync<State>(new Js(cameBack: null), "requests"));
    }

    private sealed class Js(bool? cameBack) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            cameBack is { } back && identifier == "sonnuage.cameBack"
                ? ValueTask.FromResult((TValue)(object)back)
                : throw new JSException($"Could not find '{identifier}'");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
