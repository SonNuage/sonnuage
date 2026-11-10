using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SonNuage.UI.Components;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>
/// Panneaux propres à un hôte (réglages de l'appli de bureau, avis de mise à jour) : l'hôte les déclare dans
/// IPlatformInfo, l'interface partagée les affiche sans les connaître. Le web et Android n'en déclarent aucun.
/// </summary>
public sealed class HostComponentsTests
{
    [Fact]
    public async Task Each_host_component_is_rendered_in_order()
    {
        Assert.Equal("<p>first</p><p>second</p>", await RenderAsync([typeof(First), typeof(Second)]));
    }

    [Fact]
    public async Task Without_host_components_nothing_is_rendered()
    {
        Assert.Equal("", await RenderAsync([]));
    }

    [Fact]
    public void Hosts_declare_no_component_unless_they_say_so()
    {
        IPlatformInfo web = new PlainPlatform();

        Assert.Empty(web.SettingsSections);
        Assert.Empty(web.Notices);
    }

    private static async Task<string> RenderAsync(IReadOnlyList<Type> types)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<HostComponents>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(HostComponents.Types)] = types,
            }));
            return output.ToHtmlString();
        });
    }

    private sealed class PlainPlatform : IPlatformInfo
    {
        public bool HasMeteredNetworkDetection => false;
        public string Name => "web";
    }

    private sealed class First : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<p>first</p>");
    }

    private sealed class Second : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<p>second</p>");
    }
}
