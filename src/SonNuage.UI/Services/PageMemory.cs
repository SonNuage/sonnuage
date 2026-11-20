using Microsoft.JSInterop;

namespace SonNuage.UI.Services;

/// <summary>
/// Ce qu'une page affichait (résultats, filtres, pages déjà chargées), retrouvé quand on y revient par le retour
/// arrière ; une page ouverte autrement repart de zéro. La position dans la page, elle, est rendue par ui.js.
/// Le JS est passé à chaque appel : dans les hôtes natifs, IJSRuntime appartient à la vue, pas à l'appli.
/// </summary>
public sealed class PageMemory
{
    private readonly Dictionary<string, object> _pages = [];

    public void Keep<T>(string key, T state) where T : class => _pages[key] = state;

    /// <summary>L'état gardé pour cette page si l'on y revient par l'historique, sinon null.</summary>
    public async Task<T?> RecallAsync<T>(IJSRuntime js, string key) where T : class
    {
        if (_pages.GetValueOrDefault(key) is not T state) return null;
        try
        {
            return await js.InvokeAsync<bool>("sonnuage.cameBack") ? state : null;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Hôte sans ui.js : on ne sait pas d'où l'on vient, la page repart de zéro.
            return null;
        }
    }
}
