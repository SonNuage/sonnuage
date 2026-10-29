using System.Net.Http.Json;
using System.Text.Json;
using Localify.Contracts;

namespace Localify.UI.Services;

/// <summary>
/// Détient les jetons du compte actif, les persiste et les renouvelle avant expiration.
/// Chaque compte a sa propre session (<see cref="Slot"/>) : ses jetons, sa clé de stockage, son serveur et son verrou
/// de renouvellement (un seul refresh à la fois par compte, les refresh tokens étant à usage unique côté serveur).
/// Un renouvellement lancé avant un changement de compte se termine donc dans la session du compte d'origine.
/// </summary>
public sealed class AuthSession
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MediaRefreshMargin = TimeSpan.FromHours(2);

    private readonly IKeyValueStore _store;
    private readonly IServerEndpoint _endpoint;
    private readonly AccountStore _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Dictionary<string, Slot> _slots = [];

    public AuthSession(IKeyValueStore store, IServerEndpoint endpoint, AccountStore accounts, IHttpClientFactory httpFactory)
    {
        _store = store;
        _endpoint = endpoint;
        _accounts = accounts;
        _httpFactory = httpFactory;
        accounts.ActiveChanged += () => Changed?.Invoke();
        accounts.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public UserDto? User => Current?.Tokens?.User;
    public bool IsAuthenticated => Current?.Tokens is not null;
    public bool IsAdmin => User?.Role == Roles.Admin;
    public string? MediaToken => Current?.Tokens?.MediaToken;

    /// <summary>Session du compte actif (mono-compte : la session unique). Null tant qu'aucun compte n'est actif.</summary>
    private Slot? Current
    {
        get
        {
            if (_accounts.IsMultiAccount && _accounts.Active is null) return null;
            var key = _accounts.SessionKey;
            lock (_slots)
            {
                if (!_slots.TryGetValue(key, out var slot)) _slots[key] = slot = new Slot(key, _accounts.Active?.Server);
                return slot;
            }
        }
    }

    public async Task EnsureLoadedAsync()
    {
        await _accounts.LoadAsync();
        if (Current is { } slot) await LoadAsync(slot);
    }

    private async Task LoadAsync(Slot slot)
    {
        await (slot.Loading ??= LoadCoreAsync());

        async Task LoadCoreAsync() => slot.Tokens = await ReadStoredAsync(slot);
    }

    private async Task<TokenResponse?> ReadStoredAsync(Slot slot)
    {
        var raw = await _store.GetAsync(slot.Key);
        if (raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<TokenResponse>(raw, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            await _store.SetAsync(slot.Key, null);
            return null;
        }
    }

    /// <summary>
    /// Jetons renouvelés entre-temps par un autre onglet (le stockage est partagé) : on les reprend au lieu de
    /// renouveler avec un refresh token déjà consommé, que le serveur prendrait pour un vol.
    /// </summary>
    private async Task<TokenResponse?> AdoptSharedAsync(Slot slot, TokenResponse stale)
    {
        var stored = await ReadStoredAsync(slot);
        if (stored is null || stored.RefreshToken == stale.RefreshToken || stored.User.Id != stale.User.Id) return null;
        slot.Tokens = stored;
        Changed?.Invoke();
        return stored;
    }

    /// <summary>
    /// Connexion au serveur de l'écran de connexion. En multi-comptes, le compte est créé (ou retrouvé) et devient actif.
    /// </summary>
    public async Task<string?> LoginAsync(string username, string password, string deviceName)
    {
        await _accounts.LoadAsync();
        var server = _accounts.IsMultiAccount ? _accounts.LoginServer : _endpoint.BaseAddress;
        if (server is null) return Text("errors.serverNotConfigured");

        using var http = CreateClient(server);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(username, password, deviceName), JsonDefaults.Options);
        }
        catch (HttpRequestException ex)
        {
            return Text("login.unreachable", ("message", ex.Message));
        }
        if ((int)response.StatusCode == 429) return Text("login.tooManyAttempts");
        if (!response.IsSuccessStatusCode) return Text("login.invalid");
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonDefaults.Options);
        if (tokens is null) return Text("login.invalid");

        if (!_accounts.IsMultiAccount)
        {
            await SetAsync(Current!, tokens);
            return null;
        }
        // Jetons rangés avant d'activer le compte : à l'annonce du changement, sa session est déjà prête.
        var account = await _accounts.SaveSignedInAsync(server, tokens.User);
        var slot = SlotFor(account);
        slot.Loading = Task.CompletedTask;
        await SetAsync(slot, tokens);
        await _accounts.SwitchAsync(account.Id);
        return null;
    }

    /// <summary>Déconnecte le compte actif (et l'oublie en multi-comptes).</summary>
    public Task LogoutAsync() => _accounts.IsMultiAccount && _accounts.Active is { } active ? LogoutAsync(active) : LogoutSlotAsync(Current);

    /// <summary>Déconnecte un compte précis : son refresh token est révoqué, ses données locales oubliées.</summary>
    public async Task LogoutAsync(Account account)
    {
        var slot = SlotFor(account);
        await LoadAsync(slot);
        await LogoutSlotAsync(slot);
        lock (_slots) _slots.Remove(slot.Key);
        await _accounts.RemoveAsync(account.Id);
    }

    private async Task LogoutSlotAsync(Slot? slot)
    {
        if (slot is null) return;
        var tokens = slot.Tokens;
        await SetAsync(slot, null);
        if (tokens is null || ServerOf(slot) is not { } server) return;
        try
        {
            using var http = CreateClient(server);
            await http.PostAsJsonAsync("api/auth/logout", new RefreshRequest(tokens.RefreshToken), JsonDefaults.Options);
        }
        catch (HttpRequestException)
        {
        }
    }

    /// <summary>Jeton d'accès valide, renouvelé si nécessaire. Null si la session est perdue.</summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        await EnsureLoadedAsync();
        if (Current is not { Tokens: { } tokens } slot) return null;
        if (!forceRefresh && tokens.AccessTokenExpiresAt - DateTimeOffset.UtcNow > RefreshMargin) return tokens.AccessToken;
        return (await RefreshAsync(slot, tokens))?.AccessToken;
    }

    /// <summary>Jeton média encore valide pour quelques heures (les URL d'une file d'attente vivent longtemps).</summary>
    public async Task<string?> GetMediaTokenAsync()
    {
        await EnsureLoadedAsync();
        if (Current is not { Tokens: { } tokens } slot) return null;
        if (tokens.MediaTokenExpiresAt - DateTimeOffset.UtcNow > MediaRefreshMargin) return tokens.MediaToken;
        return (await RefreshAsync(slot, tokens))?.MediaToken ?? tokens.MediaToken;
    }

    private async Task<TokenResponse?> RefreshAsync(Slot slot, TokenResponse stale)
    {
        await slot.RefreshLock.WaitAsync();
        try
        {
            // Un autre appel a déjà renouvelé pendant qu'on attendait.
            if (!ReferenceEquals(slot.Tokens, stale)) return slot.Tokens;
            if (await AdoptSharedAsync(slot, stale) is { } shared)
            {
                if (shared.AccessTokenExpiresAt - DateTimeOffset.UtcNow > RefreshMargin) return shared;
                stale = shared;
            }
            if (ServerOf(slot) is not { } server) return null;

            using var http = CreateClient(server);
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync("api/auth/refresh", new RefreshRequest(stale.RefreshToken), JsonDefaults.Options);
            }
            catch (HttpRequestException)
            {
                // Hors ligne : on garde la session, l'appel suivant réessaiera.
                return stale.AccessTokenExpiresAt > DateTimeOffset.UtcNow ? stale : null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Perdu la course contre un autre onglet : ses jetons sont dans le stockage partagé.
                if (await AdoptSharedAsync(slot, stale) is { } winner) return winner;
                await SetAsync(slot, null);
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;

            var fresh = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonDefaults.Options);
            await SetAsync(slot, fresh);
            return fresh;
        }
        finally
        {
            slot.RefreshLock.Release();
        }
    }

    private async Task SetAsync(Slot slot, TokenResponse? tokens)
    {
        slot.Tokens = tokens;
        await _store.SetAsync(slot.Key, tokens is null ? null : JsonSerializer.Serialize(tokens, JsonDefaults.Options));
        Changed?.Invoke();
    }

    /// <summary>Après un changement du compte (langue préférée…) : la session garde l'utilisateur à jour.</summary>
    public async Task UpdateUserAsync(UserDto user)
    {
        if (Current is { Tokens: { } t } slot && t.User.Id == user.Id) await SetAsync(slot, t with { User = user });
    }

    private static string Text(string key, params (string, object?)[] args) =>
        Localizer.Current?[key, args] ?? Localify.Contracts.Localization.Translations.Instance.Get("en", key, args);

    private Slot SlotFor(Account account)
    {
        lock (_slots)
        {
            if (!_slots.TryGetValue(account.SessionKey, out var slot)) _slots[account.SessionKey] = slot = new Slot(account.SessionKey, account.Server);
            return slot;
        }
    }

    /// <summary>Le serveur d'un compte est fixe ; en mono-compte c'est l'adresse de l'hôte (l'origine sur le web).</summary>
    private Uri? ServerOf(Slot slot) => slot.Server ?? _endpoint.BaseAddress;

    private HttpClient CreateClient(Uri server)
    {
        var http = _httpFactory.CreateClient("localify-anonymous");
        http.BaseAddress = server;
        return http;
    }

    private sealed class Slot(string key, Uri? server)
    {
        public string Key { get; } = key;
        public Uri? Server { get; } = server;
        public SemaphoreSlim RefreshLock { get; } = new(1, 1);
        public TokenResponse? Tokens { get; set; }
        public Task? Loading { get; set; }
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
