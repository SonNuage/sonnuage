using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Nom de cet appareil pour les autres appareils du compte (« En lecture sur Pixel de Phil ») : celui donné dans
/// Réglages, sinon celui du système (<see cref="IPlatformInfo.DeviceNameAsync"/>). Gardé sur l'appareil, pour tous
/// ses comptes.
/// </summary>
public sealed class DeviceIdentity(IKeyValueStore store, IPlatformInfo platform)
{
    private const string NameKey = "sonnuage.device.name";
    private Task? _loading;

    public string Name { get; private set; } = platform.Name;

    /// <summary>Nom choisi dans Réglages (null : celui du système).</summary>
    public string? CustomName { get; private set; }

    public string Platform => platform.Name;

    public event Action? Changed;

    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        CustomName = Clean(await store.GetAsync(NameKey));
        Name = CustomName ?? Clean(await platform.DeviceNameAsync()) ?? platform.Name;
    }

    /// <summary>Nouveau nom ; vide : retour au nom du système.</summary>
    public async Task RenameAsync(string? name)
    {
        await LoadAsync();
        await store.SetAsync(NameKey, Clean(name));
        _loading = LoadCoreAsync();
        await _loading;
        Changed?.Invoke();
    }

    public static string? Clean(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= DeviceLink.MaxNameLength ? trimmed : trimmed[..DeviceLink.MaxNameLength];
    }
}

/// <summary>Nom par défaut d'un navigateur, d'après son agent utilisateur : « Firefox · Windows ».</summary>
public static class DeviceNames
{
    public static string FromUserAgent(string? userAgent)
    {
        var ua = userAgent ?? "";
        var browser =
            ua.Contains("Edg/") ? "Edge" :
            ua.Contains("OPR/") ? "Opera" :
            ua.Contains("Firefox/") ? "Firefox" :
            ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome" :
            ua.Contains("Safari/") ? "Safari" : "Navigateur";
        var system =
            ua.Contains("Android") ? "Android" :
            ua.Contains("iPhone") ? "iPhone" :
            ua.Contains("iPad") ? "iPad" :
            ua.Contains("Windows") ? "Windows" :
            ua.Contains("Mac OS X") ? "macOS" :
            ua.Contains("CrOS") ? "ChromeOS" :
            ua.Contains("Linux") ? "Linux" : null;
        return system is null ? browser : $"{browser} · {system}";
    }
}
