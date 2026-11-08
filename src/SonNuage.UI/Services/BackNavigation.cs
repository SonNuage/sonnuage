using Microsoft.AspNetCore.Components;

namespace SonNuage.UI.Services;

public static class BackNavigation
{
    /// <summary>
    /// Retour (bouton du téléphone, geste, navigateur) : referme ce qui est ouvert (menu, fenêtre, plein écran) au lieu
    /// de quitter la page. Un lien cliqué passe ; le code qui navigue doit refermer l'élément avant d'appeler NavigateTo.
    /// </summary>
    public static IDisposable CloseOnBack(this NavigationManager nav, Func<bool> isOpen, Action close) =>
        nav.RegisterLocationChangingHandler(context =>
        {
            if (isOpen() && !context.IsNavigationIntercepted)
            {
                context.PreventNavigation();
                close();
            }
            return ValueTask.CompletedTask;
        });
}
