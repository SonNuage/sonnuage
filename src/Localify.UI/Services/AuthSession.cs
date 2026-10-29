using System.Net.Http.Json;
using System.Text.Json;
using Localify.Contracts;

namespace Localify.UI.Services;

/// <summary>
/// Détient les jetons, les persiste et les renouvelle avant expiration (un seul refresh à la fois,
/// les refresh tokens étant à usage unique côté serveur).
/// </summary>
public sealed class AuthSession(IKeyValueStore store, IServerEndpoint endpoint, IHttpClientFactory httpFactory)
{
    private const string StorageKey = "localify.session";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MediaRefreshMargin = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private TokenResponse? _tokens;
    private bool _loaded;

    public event Action? Changed;

    public UserDto? User => _tokens?.User;
    public bool IsAuthenticated => _tokens is not null;
    public bool IsAdmin => _tokens?.User.Role == Roles.Admin;
    public string? MediaToken => _tokens?.MediaToken;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var raw = await store.GetAsync(StorageKey);
        if (raw is null) return;
        try
        {
            _tokens = JsonSerializer.Deserialize<TokenResponse>(raw, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            await store.SetAsync(StorageKey, null);
        }
    }

    public async Task<string?> LoginAsync(string username, string password, string deviceName)
    {
        using var http = CreateClient();
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(username, password, deviceName), JsonDefaults.Options);
        }
        catch (HttpRequestException ex)
        {
            return $"Serveur injoignable : {ex.Message}";
        }
        if ((int)response.StatusCode == 429) return "Trop de tentatives, réessayez dans une minute.";
        if (!response.IsSuccessStatusCode) return "Identifiants invalides.";
        await SetAsync(await response.Content.ReadFromJsonAsync<TokenResponse>(JsonDefaults.Options));
        return null;
    }

    public async Task LogoutAsync()
    {
        var tokens = _tokens;
        await SetAsync(null);
        if (tokens is null) return;
        try
        {
            using var http = CreateClient();
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
        var tokens = _tokens;
        if (tokens is null) return null;
        if (!forceRefresh && tokens.AccessTokenExpiresAt - DateTimeOffset.UtcNow > RefreshMargin) return tokens.AccessToken;
        return (await RefreshAsync(tokens))?.AccessToken;
    }

    /// <summary>Jeton média encore valide pour quelques heures (les URL d'une file d'attente vivent longtemps).</summary>
    public async Task<string?> GetMediaTokenAsync()
    {
        await EnsureLoadedAsync();
        var tokens = _tokens;
        if (tokens is null) return null;
        if (tokens.MediaTokenExpiresAt - DateTimeOffset.UtcNow > MediaRefreshMargin) return tokens.MediaToken;
        return (await RefreshAsync(tokens))?.MediaToken ?? tokens.MediaToken;
    }

    private async Task<TokenResponse?> RefreshAsync(TokenResponse stale)
    {
        await _refreshLock.WaitAsync();
        try
        {
            // Un autre appel a déjà renouvelé pendant qu'on attendait.
            if (!ReferenceEquals(_tokens, stale)) return _tokens;

            using var http = CreateClient();
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
                await SetAsync(null);
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;

            var fresh = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonDefaults.Options);
            await SetAsync(fresh);
            return fresh;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task SetAsync(TokenResponse? tokens)
    {
        _tokens = tokens;
        await store.SetAsync(StorageKey, tokens is null ? null : JsonSerializer.Serialize(tokens, JsonDefaults.Options));
        Changed?.Invoke();
    }

    private HttpClient CreateClient()
    {
        var http = httpFactory.CreateClient("localify-anonymous");
        http.BaseAddress = endpoint.BaseAddress ?? throw new InvalidOperationException("Adresse du serveur non configurée");
        return http;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
