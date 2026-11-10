using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace SonNuage.UI.Components;

/// <summary>
/// Composants fournis par l'hôte (voir <see cref="Services.IPlatformInfo.SettingsSections"/> et
/// <see cref="Services.IPlatformInfo.Notices"/>), rendus dans l'ordre. L'interface partagée ne les connaît pas : ils
/// viennent de l'appli de bureau, par exemple, et injectent ce dont ils ont besoin.
/// </summary>
public sealed class HostComponents : ComponentBase
{
    [Parameter] public IReadOnlyList<Type> Types { get; set; } = [];

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        foreach (var type in Types)
        {
            builder.OpenComponent(0, type);
            builder.SetKey(type);
            builder.CloseComponent();
        }
    }
}
