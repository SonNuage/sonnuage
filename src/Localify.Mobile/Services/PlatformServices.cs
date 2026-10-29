using Localify.UI.Services;

namespace Localify.Mobile.Services;

/// <summary>Preferences pour les réglages, SecureStorage (Keystore Android) pour la session et ses jetons.</summary>
public sealed class MobileStore : IKeyValueStore
{
    private const string SecurePrefix = "localify.session";

    public async Task<string?> GetAsync(string key)
    {
        if (!key.StartsWith(SecurePrefix, StringComparison.Ordinal)) return Preferences.Default.Get<string?>(key, null);
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception)
        {
            // Clé Keystore invalidée (restauration de sauvegarde, changement de verrouillage) : on repart de zéro.
            SecureStorage.Default.Remove(key);
            return null;
        }
    }

    public async Task SetAsync(string key, string? value)
    {
        if (!key.StartsWith(SecurePrefix, StringComparison.Ordinal))
        {
            if (value is null) Preferences.Default.Remove(key);
            else Preferences.Default.Set(key, value);
            return;
        }
        if (value is null) SecureStorage.Default.Remove(key);
        else await SecureStorage.Default.SetAsync(key, value);
    }
}

/// <summary>Adresse du serveur saisie au premier lancement (ex. https://musique.example.org).</summary>
public sealed class MobileEndpoint : IServerEndpoint
{
    private const string Key = "localify.server";

    public MobileEndpoint()
    {
        var raw = Preferences.Default.Get<string?>(Key, null);
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)) BaseAddress = uri;
    }

    public Uri? BaseAddress { get; private set; }
    public bool IsConfigurable => true;
    public event Action? Changed;

    public Task SetAsync(Uri? address)
    {
        // Uri relative "api/..." doit se résoudre sous le chemin éventuel du serveur.
        if (address is not null && !address.AbsolutePath.EndsWith('/')) address = new Uri(address + "/");
        BaseAddress = address;
        if (address is null) Preferences.Default.Remove(Key);
        else Preferences.Default.Set(Key, address.ToString());
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}

public sealed class MobilePlatform : IPlatformInfo
{
    public bool HasMeteredNetworkDetection => true;
    public string Name => "android";

    /// <summary>Wi-Fi/Ethernet : qualité normale ; réseau facturé à l'usage (4G…) : qualité mobile.</summary>
    public static bool IsMetered()
    {
        var cm = (Android.Net.ConnectivityManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.ConnectivityService);
        return cm?.IsActiveNetworkMetered ?? false;
    }
}
