using Microsoft.JSInterop;

namespace Localify.UI.Services;

/// <summary>Thème de l'interface, propre à l'appareil : clair, sombre ou celui du système.</summary>
public sealed class ThemeSettings(IKeyValueStore store, IJSRuntime js)
{
    // Même clé que celle lue par ui.js au chargement de la page (pas de flash de l'autre thème).
    private const string Key = "localify.theme";

    public static readonly string[] Modes = ["system", "light", "dark"];

    public string Mode { get; private set; } = "system";

    public event Action? Changed;

    public async Task LoadAsync()
    {
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
            await js.InvokeVoidAsync("localify.setTheme", Mode);
        }
        catch (JSException)
        {
            // ui.js absent (hôte sans lui) : le thème sombre par défaut reste.
        }
    }
}
