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
        catch (Exception ex)
        {
            // Clé Keystore invalidée (sauvegarde restaurée sur un autre appareil ou après réinstallation, changement de
            // verrouillage) : le fichier chiffré est illisible. Le nettoyage peut lui-même échouer : on repart de zéro
            // sans jamais laisser l'exception remonter (elle tuait l'appli au démarrage).
            Android.Util.Log.Warn("Localify", $"SecureStorage illisible, réinitialisé : {ex}");
            try
            {
                SecureStorage.Default.Remove(key);
            }
            catch (Exception)
            {
                try { SecureStorage.Default.RemoveAll(); } catch (Exception) { }
            }
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
        try
        {
            if (value is null) SecureStorage.Default.Remove(key);
            else await SecureStorage.Default.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            // Même cause que ci-dessus : on vide le stockage sécurisé et on réessaie une fois.
            Android.Util.Log.Warn("Localify", $"SecureStorage illisible, réinitialisé : {ex}");
            try { SecureStorage.Default.RemoveAll(); } catch (Exception) { }
            if (value is not null) await SecureStorage.Default.SetAsync(key, value);
        }
    }
}

/// <summary>
/// Adresse du serveur : celle du compte actif, ou celle saisie pour le compte en cours d'ajout tant qu'aucun compte
/// n'est actif. Pendant un ajout, l'API reste donc adressée au compte actif (lecture en arrière-plan comprise).
/// </summary>
public sealed class AccountEndpoint : IServerEndpoint
{
    private readonly AccountStore _accounts;

    public AccountEndpoint(AccountStore accounts)
    {
        _accounts = accounts;
        accounts.ActiveChanged += () => Changed?.Invoke();
        accounts.Changed += () => Changed?.Invoke();
    }

    public Uri? BaseAddress => _accounts.Active?.Server ?? _accounts.PendingServer;
    public bool IsConfigurable => true;
    public event Action? Changed;

    public Task SetAsync(Uri? address)
    {
        // Uri relative "api/..." doit se résoudre sous le chemin éventuel du serveur.
        if (address is not null && !address.AbsolutePath.EndsWith('/')) address = new Uri(address + "/");
        _accounts.SetPendingServer(address);
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
