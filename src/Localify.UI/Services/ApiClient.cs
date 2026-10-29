using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Localify.Contracts;

namespace Localify.UI.Services;

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Client typé de l'API. Ajoute le jeton, réessaie une fois après renouvellement sur 401.</summary>
public sealed class ApiClient(IHttpClientFactory httpFactory, IServerEndpoint endpoint, AuthSession auth)
{
    // ---- Bibliothèque ----
    public Task<ServerInfoDto?> InfoAsync() => GetAsync<ServerInfoDto>("api/info");
    public Task<HomeDto?> HomeAsync() => GetAsync<HomeDto>("api/home");
    public Task<PagedResult<AlbumDto>?> AlbumsAsync(string sort, int offset, int limit, string? q = null) =>
        GetAsync<PagedResult<AlbumDto>>($"api/albums?sort={sort}&offset={offset}&limit={limit}{Q(q)}");
    public Task<PagedResult<ArtistDto>?> ArtistsAsync(int offset, int limit, string? q = null) =>
        GetAsync<PagedResult<ArtistDto>>($"api/artists?offset={offset}&limit={limit}{Q(q)}");
    public Task<AlbumDetailDto?> AlbumAsync(Guid id) => GetAsync<AlbumDetailDto>($"api/albums/{id}");
    public Task<ArtistDetailDto?> ArtistAsync(Guid id) => GetAsync<ArtistDetailDto>($"api/artists/{id}");
    public Task<List<TrackDto>?> ArtistTracksAsync(Guid id) => GetAsync<List<TrackDto>>($"api/artists/{id}/tracks");
    public Task<List<TrackDto>?> RandomTracksAsync(int count) => GetAsync<List<TrackDto>>($"api/tracks/random?count={count}");
    public Task<List<TrackDto>?> LookupTracksAsync(IReadOnlyList<Guid> ids) => SendAsync<List<TrackDto>>(HttpMethod.Post, "api/tracks/lookup", ids);
    public Task<SearchResultDto?> SearchAsync(string q, int limit = 20) => GetAsync<SearchResultDto>($"api/search?limit={limit}&q={Uri.EscapeDataString(q)}");
    public Task<List<string>?> SuggestAsync(string q, int limit = 8) => GetAsync<List<string>>($"api/search/suggest?limit={limit}&q={Uri.EscapeDataString(q)}");

    // ---- Moi ----
    public Task<List<TrackDto>?> FavoritesAsync() => GetAsync<List<TrackDto>>("api/me/favorites");
    public Task SetFavoriteAsync(Guid trackId, bool favorite) =>
        SendAsync(favorite ? HttpMethod.Put : HttpMethod.Delete, $"api/me/favorites/{trackId}");
    public Task<List<TrackDto>?> RecentAsync(int count = 50) => GetAsync<List<TrackDto>>($"api/me/recent?count={count}");
    public Task ReportPlayAsync(PlayEventRequest play) => SendAsync(HttpMethod.Post, "api/me/plays", play);
    public Task ChangePasswordAsync(string current, string next) =>
        SendAsync(HttpMethod.Post, "api/auth/password", new ChangePasswordRequest(current, next));

    // ---- Playlists ----
    public Task<List<PlaylistDto>?> PlaylistsAsync() => GetAsync<List<PlaylistDto>>("api/playlists");
    public Task<PlaylistDetailDto?> PlaylistAsync(Guid id) => GetAsync<PlaylistDetailDto>($"api/playlists/{id}");
    public Task<PlaylistDto?> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds = null) =>
        SendAsync<PlaylistDto>(HttpMethod.Post, "api/playlists", new CreatePlaylistRequest(name, null, trackIds));
    public Task UpdatePlaylistAsync(Guid id, string? name, string? description) =>
        SendAsync(HttpMethod.Patch, $"api/playlists/{id}", new UpdatePlaylistRequest(name, description));
    public Task DeletePlaylistAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/playlists/{id}");
    public Task AddToPlaylistAsync(Guid id, IReadOnlyList<Guid> trackIds) =>
        SendAsync(HttpMethod.Post, $"api/playlists/{id}/tracks", new AddPlaylistTracksRequest(trackIds, null));
    public Task RemovePlaylistEntryAsync(Guid id, Guid entryId) => SendAsync(HttpMethod.Delete, $"api/playlists/{id}/entries/{entryId}");
    public Task MovePlaylistEntryAsync(Guid id, Guid entryId, int position) =>
        SendAsync(HttpMethod.Post, $"api/playlists/{id}/entries/{entryId}/move", new MovePlaylistEntryRequest(position));

    // ---- Admin ----
    public Task<LibraryStatsDto?> AdminStatsAsync() => GetAsync<LibraryStatsDto>("api/admin/stats");
    public Task AdminScanAsync() => SendAsync(HttpMethod.Post, "api/admin/scan");
    public Task AdminClearCacheAsync() => SendAsync(HttpMethod.Delete, "api/admin/cache/transcode");
    public Task<PagedResult<TrackMetadataDto>?> AdminTracksAsync(string? q, Guid? albumId, int offset, int limit) =>
        GetAsync<PagedResult<TrackMetadataDto>>($"api/admin/tracks?offset={offset}&limit={limit}{Q(q)}{(albumId is { } a ? $"&albumId={a}" : "")}");
    public Task<TrackMetadataDto?> AdminUpdateTrackAsync(Guid id, UpdateTrackMetadataRequest req) =>
        SendAsync<TrackMetadataDto>(HttpMethod.Put, $"api/admin/tracks/{id}/metadata", req);
    public Task AdminDeleteTrackAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/tracks/{id}");
    public Task<AlbumDto?> AdminUpdateAlbumAsync(Guid id, UpdateAlbumRequest req) => SendAsync<AlbumDto>(HttpMethod.Put, $"api/admin/albums/{id}", req);
    public Task AdminSetAlbumCoverAsync(Guid id, Stream image, string contentType) =>
        SendRawAsync(HttpMethod.Put, $"api/admin/albums/{id}/cover", image, contentType);
    public Task<UploadSessionDto?> AdminStartUploadAsync(StartUploadRequest req) => SendAsync<UploadSessionDto>(HttpMethod.Post, "api/admin/uploads", req);
    public Task<UploadSessionDto?> AdminUploadStatusAsync(Guid id) => GetAsync<UploadSessionDto>($"api/admin/uploads/{id}");
    public Task<UploadSessionDto?> AdminUploadChunkAsync(Guid id, long offset, ReadOnlyMemory<byte> data) =>
        SendRawAsync<UploadSessionDto>(HttpMethod.Put, $"api/admin/uploads/{id}?offset={offset}", new ReadOnlyMemoryContent(data), "application/octet-stream");
    public Task<List<TrackDto>?> AdminCompleteUploadAsync(Guid id) => SendAsync<List<TrackDto>>(HttpMethod.Post, $"api/admin/uploads/{id}/complete");
    public Task AdminCancelUploadAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/uploads/{id}");
    public Task<List<UserDto>?> AdminUsersAsync() => GetAsync<List<UserDto>>("api/admin/users");
    public Task<UserDto?> AdminCreateUserAsync(CreateUserRequest req) => SendAsync<UserDto>(HttpMethod.Post, "api/admin/users", req);
    public Task<UserDto?> AdminUpdateUserAsync(Guid id, UpdateUserRequest req) => SendAsync<UserDto>(HttpMethod.Patch, $"api/admin/users/{id}", req);
    public Task AdminDeleteUserAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/users/{id}");

    // ---- URL média (jeton en query) ----

    public string? CoverUrl(Guid? albumId, int size)
    {
        if (albumId is null || endpoint.BaseAddress is null || auth.MediaToken is null) return null;
        return new Uri(endpoint.BaseAddress, $"api/albums/{albumId}/cover?size={size}&t={auth.MediaToken}").ToString();
    }

    public async Task<string?> StreamUrlAsync(Guid trackId, StreamQuality quality)
    {
        var token = await auth.GetMediaTokenAsync();
        if (token is null || endpoint.BaseAddress is null) return null;
        return new Uri(endpoint.BaseAddress, $"api/tracks/{trackId}/stream?quality={quality.ToWire()}&t={token}").ToString();
    }

    // ---- Plomberie ----

    private static string Q(string? q) => string.IsNullOrWhiteSpace(q) ? "" : $"&q={Uri.EscapeDataString(q)}";

    private async Task<T?> GetAsync<T>(string url)
    {
        using var response = await SendCoreAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendCoreAsync(() => Build(method, url, body));
        await EnsureSuccessAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options);
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendCoreAsync(() => Build(method, url, body));
        await EnsureSuccessAsync(response);
    }

    private async Task SendRawAsync(HttpMethod method, string url, Stream content, string contentType)
    {
        // Flux non rejouable : pas de second essai après refresh, on rafraîchit avant.
        await auth.GetAccessTokenAsync();
        using var response = await SendCoreAsync(() =>
        {
            var c = new StreamContent(content);
            c.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return new HttpRequestMessage(method, url) { Content = c };
        }, retry: false);
        await EnsureSuccessAsync(response);
    }

    private async Task<T?> SendRawAsync<T>(HttpMethod method, string url, HttpContent content, string contentType)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        await auth.GetAccessTokenAsync();
        using var response = await SendCoreAsync(() => new HttpRequestMessage(method, url) { Content = content }, retry: false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options);
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonDefaults.Options);
        return request;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> build, bool retry = true)
    {
        var http = httpFactory.CreateClient("localify");
        http.BaseAddress = endpoint.BaseAddress ?? throw new ApiException(0, "Adresse du serveur non configurée.");

        var token = await auth.GetAccessTokenAsync() ?? throw new ApiException(HttpStatusCode.Unauthorized, "Session expirée.");
        var request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized && retry)
        {
            response.Dispose();
            token = await auth.GetAccessTokenAsync(forceRefresh: true) ?? throw new ApiException(HttpStatusCode.Unauthorized, "Session expirée.");
            request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await http.SendAsync(request);
        }
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string message;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(JsonDefaults.Options);
            message = problem?.Detail ?? problem?.Title ?? response.ReasonPhrase ?? "Erreur";
        }
        catch (Exception)
        {
            message = response.ReasonPhrase ?? $"Erreur {(int)response.StatusCode}";
        }
        throw new ApiException(response.StatusCode, message);
    }

    private sealed record ProblemPayload(string? Title, string? Detail);
}
