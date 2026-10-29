using System.Text.Json;
using System.Text.Json.Serialization;
using Localify.Contracts;

namespace Localify.UI.Services;

/// <summary>
/// Un compte : un utilisateur sur un serveur. Ses jetons sont rangés à part, sous <see cref="SessionKey"/>
/// (SecureStorage sur Android), jamais dans la liste des comptes.
/// Un appareil appartient à un compte : un futur lien de lecture serveur (IPlaybackLink via SignalR) s'ouvrira
/// avec les jetons du compte actif et se rouvrira au changement de compte.
/// </summary>
public sealed record Account(Guid Id, Uri Server, Guid UserId, string Username)
{
    [JsonIgnore]
    public string Label => $"{Username} · {Host}";

    /// <summary>Hôte, avec le port s'il n'est pas celui par défaut (deux serveurs sur la même machine).</summary>
    [JsonIgnore]
    public string Host => Server.IsDefaultPort ? Server.Host : $"{Server.Host}:{Server.Port}";

    [JsonIgnore]
    public string SessionKey => AccountStore.Scoped(AccountStore.SessionBaseKey, Id);
}

/// <summary>
/// Comptes connectés et compte actif. Le compte actif fixe le serveur, la bibliothèque affichée et la lecture ;
/// les données qui lui sont propres (session, file, réglages de lecture) sont rangées sous des clés suffixées par
/// son identifiant (<see cref="ScopedKey"/>).
/// <para>
/// Sur le web (mono-compte, même origine), rien n'est géré ici : <see cref="ScopedKey"/> renvoie la clé telle
/// quelle et la session reste sous « localify.session », comme avant.
/// </para>
/// </summary>
public sealed class AccountStore(IKeyValueStore store, bool multiAccount)
{
    public const string SessionBaseKey = "localify.session";
    public const string AccountsKey = "localify.accounts";
    public const string LegacyServerKey = "localify.server";

    /// <summary>Données par compte reprises de l'ancienne installation mono-compte.</summary>
    public static readonly string[] MigratedKeys = ["localify.queue", "localify.quality", "localify.quality.mobile", "localify.prefetch"];

    private readonly List<Account> _accounts = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _loading;

    public bool IsMultiAccount => multiAccount;
    public IReadOnlyList<Account> Accounts => _accounts;
    public Account? Active { get; private set; }

    /// <summary>Ajout de compte en cours (écran serveur puis connexion), le compte actif restant utilisé en attendant.</summary>
    public bool IsAdding { get; private set; }

    /// <summary>Serveur choisi pour le compte en cours d'ajout.</summary>
    public Uri? PendingServer { get; private set; }

    /// <summary>Le compte actif a changé (ou a été retiré). Les données par compte sont à recharger.</summary>
    public event Action? ActiveChanged;

    /// <summary>Liste ou état d'ajout modifiés (sans changement de compte actif).</summary>
    public event Action? Changed;

    /// <summary>L'écran « Votre serveur » est à afficher.</summary>
    public bool NeedsServer => multiAccount && (IsAdding || Active is null) && PendingServer is null;

    /// <summary>Serveur auquel l'écran de connexion s'adresse : celui du compte en cours d'ajout, sinon celui du compte actif.</summary>
    public Uri? LoginServer => PendingServer ?? (IsAdding ? null : Active?.Server);

    /// <summary>L'ajout peut être annulé (il reste un compte vers lequel revenir).</summary>
    public bool CanCancelAdd => IsAdding && Active is not null;

    public string SessionKey => Active?.SessionKey ?? SessionBaseKey;

    /// <summary>Clé de stockage propre au compte actif (identique à <paramref name="baseKey"/> en mono-compte).</summary>
    public string ScopedKey(string baseKey) => multiAccount && Active is { } a ? Scoped(baseKey, a.Id) : baseKey;

    public static string Scoped(string baseKey, Guid accountId) => $"{baseKey}.{accountId:N}";

    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        if (!multiAccount) return;
        var raw = await store.GetAsync(AccountsKey);
        if (raw is null)
        {
            await MigrateAsync();
            return;
        }
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(raw, JsonDefaults.Options);
            _accounts.AddRange(saved?.Accounts ?? []);
            Active = _accounts.FirstOrDefault(a => a.Id == saved?.Active) ?? _accounts.FirstOrDefault();
        }
        catch (JsonException)
        {
            await store.SetAsync(AccountsKey, null);
        }
    }

    /// <summary>
    /// Première ouverture après la mise à jour multi-comptes : la session unique (« localify.server » +
    /// « localify.session ») devient le premier compte, avec sa file et ses réglages de lecture.
    /// </summary>
    private async Task MigrateAsync()
    {
        var server = await store.GetAsync(LegacyServerKey) is { } rawServer && Uri.TryCreate(rawServer, UriKind.Absolute, out var s) ? s : null;
        var session = await store.GetAsync(SessionBaseKey);
        TokenResponse? tokens = null;
        try
        {
            if (session is not null) tokens = JsonSerializer.Deserialize<TokenResponse>(session, JsonDefaults.Options);
        }
        catch (JsonException)
        {
        }

        if (server is not null && tokens is not null)
        {
            var account = new Account(Guid.NewGuid(), server, tokens.User.Id, tokens.User.Username);
            await store.SetAsync(account.SessionKey, session);
            foreach (var key in MigratedKeys)
            {
                if (await store.GetAsync(key) is { } value) await store.SetAsync(Scoped(key, account.Id), value);
                await store.SetAsync(key, null);
            }
            _accounts.Add(account);
            Active = account;
        }
        else if (server is not null)
        {
            // Serveur choisi mais jamais connecté (ou session illisible) : on reprend directement à l'écran de connexion.
            PendingServer = server;
        }

        await SaveAsync();
        await store.SetAsync(SessionBaseKey, null);
        await store.SetAsync(LegacyServerKey, null);
    }

    public void BeginAdd()
    {
        IsAdding = true;
        PendingServer = null;
        Changed?.Invoke();
    }

    public void CancelAdd()
    {
        IsAdding = false;
        PendingServer = null;
        Changed?.Invoke();
    }

    public void SetPendingServer(Uri? server)
    {
        PendingServer = server;
        // « Changer de serveur » depuis la reconnexion d'un compte expiré : c'est un ajout.
        if (server is null && Active is not null) IsAdding = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Enregistre le compte après une connexion réussie (le même utilisateur sur le même serveur est réutilisé,
    /// la reconnexion d'un compte expiré ne crée donc pas de doublon). Ne l'active pas : voir <see cref="SwitchAsync"/>.
    /// </summary>
    public async Task<Account> SaveSignedInAsync(Uri server, UserDto user)
    {
        await LoadAsync();
        var existing = _accounts.FirstOrDefault(a => a.UserId == user.Id && SameServer(a.Server, server));
        var account = new Account(existing?.Id ?? Guid.NewGuid(), server, user.Id, user.Username);
        if (existing is null) _accounts.Add(account);
        else _accounts[_accounts.IndexOf(existing)] = account;
        if (ReferenceEquals(Active, existing) && existing is not null) Active = account;
        await SaveAsync();
        return account;
    }

    /// <summary>Rend <paramref name="id"/> actif et termine un éventuel ajout en cours.</summary>
    public async Task SwitchAsync(Guid id)
    {
        await LoadAsync();
        var target = _accounts.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Compte inconnu.");
        var changed = Active?.Id != target.Id;
        Active = target;
        IsAdding = false;
        PendingServer = null;
        await SaveAsync();
        if (changed) ActiveChanged?.Invoke();
        else Changed?.Invoke();
    }

    /// <summary>Oublie le compte et ses données locales. S'il était actif, le premier compte restant prend le relais.</summary>
    public async Task RemoveAsync(Guid id)
    {
        await LoadAsync();
        var account = _accounts.FirstOrDefault(a => a.Id == id);
        if (account is null) return;
        _accounts.Remove(account);
        await store.SetAsync(account.SessionKey, null);
        foreach (var key in MigratedKeys) await store.SetAsync(Scoped(key, id), null);

        var wasActive = Active?.Id == id;
        if (wasActive) Active = _accounts.FirstOrDefault();
        await SaveAsync();
        if (wasActive) ActiveChanged?.Invoke();
        else Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await store.SetAsync(AccountsKey, JsonSerializer.Serialize(new Saved(Active?.Id, [.. _accounts]), JsonDefaults.Options));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static bool SameServer(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private sealed record Saved(Guid? Active, List<Account> Accounts);
}

/// <summary>Vue du stockage limitée au compte actif : chaque clé est suffixée par son identifiant (inchangée en mono-compte).</summary>
public sealed class ScopedStore(IKeyValueStore store, AccountStore accounts) : IKeyValueStore
{
    public Task<string?> GetAsync(string key) => store.GetAsync(accounts.ScopedKey(key));
    public Task SetAsync(string key, string? value) => store.SetAsync(accounts.ScopedKey(key), value);
}
