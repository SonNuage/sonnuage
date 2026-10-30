using Microsoft.JSInterop;

namespace SonNuage.UI.Services;

/// <summary>Thème de l'interface, propre à l'appareil : clair, sombre ou celui du système.</summary>
public sealed class ThemeSettings(IKeyValueStore store)
{
    // Même clé que celle lue par ui.js au chargement de la page (pas de flash de l'autre thème).
    private const string Key = "sonnuage.theme";

    public static readonly string[] Modes = ["system", "light", "dark"];

    public string Mode { get; private set; } = "system";

    public event Action? Changed;

    // Fourni par l'interface (LoadAsync), comme pour Localizer : sous Android, IJSRuntime est propre à la WebView
    // (scoped) et celui d'un singleton n'y est pas rattaché.
    private IJSRuntime? _js;

    /// <param name="js">Runtime JS de la page (celui injecté dans le composant racine).</param>
    public async Task LoadAsync(IJSRuntime js)
    {
        _js = js;
        var saved = await store.GetAsync(Key);
        Mode = saved is "light" or "dark" ? saved : "system";
        await ApplyAsync();
    }

    public async Task SetAsync(string mode)
    {
        Mode = Modes.Contains(mode) ? mode : "system";
        await store.SetAsync(Key, Mode == "system" ? null : Mode);
        await ApplyAsync();
        Changed?.Invoke();
    }

    private async Task ApplyAsync()
    {
        try
        {
            if (_js is not null) await _js.InvokeVoidAsync("sonnuage.setTheme", Mode);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // ui.js absent (hôte sans lui) : le thème sombre par défaut reste.
        }
    }
}
