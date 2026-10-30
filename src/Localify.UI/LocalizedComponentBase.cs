using Microsoft.AspNetCore.Components;

namespace Localify.UI;

/// <summary>
/// Base de tous les composants de l'interface (voir _Imports.razor). La langue leur arrive en cascade depuis App :
/// quand elle change, Blazor redessine chacun avec les nouveaux textes, sans rien recréer (page, saisie et lecture
/// restent en place).
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase
{
    public const string LanguageCascade = "UiLanguage";

    [CascadingParameter(Name = LanguageCascade)] public string? UiLanguage { get; set; }
}
