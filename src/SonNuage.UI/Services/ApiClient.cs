using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Client typé de l'API. Ajoute le jeton, réessaie une fois après renouvellement sur 401.</summary>
public sealed class ApiClient(IHttpClientFactory httpFactory, IServerEndpoint endpoint, AuthSession auth) : IPlaybackPreferencesApi
{
    // ---- Bibliothèque ----
    public Task<ServerInfoDto?> InfoAsync() => GetAsync<ServerInfoDto>("api/info");
    public Task<UserDto?> SetLanguageAsync(string? language) => SendAsync<UserDto>(HttpMethod.Put, "api/auth/language", new SetLanguageRequest(language));
    public Task<HomeDto?> HomeAsync() => GetAsync<HomeDto>("api/home");
    public Task<PagedResult<AlbumDto>?> AlbumsAsync(string sort, int offset, int limit, string? q = null) =>
        GetAsync<PagedResult<AlbumDto>>($"api/albums?sort={sort}&offset={offset}&limit={limit}{Q(q)}");
    public Task<PagedResult<ArtistDto>?> ArtistsAsync(int offset, int limit, string? q = null) =>
        GetAsync<PagedResult<ArtistDto>>($"api/artists?offset={offset}&limit={limit}{Q(q)}");
    public Task<AlbumDetailDto?> AlbumAsync(Guid id) => GetAsync<AlbumDetailDto>($"api/albums/{id}");
    public Task<ArtistDetailDto?> ArtistAsync(Guid id) => GetAsync<ArtistDetailDto>($"api/artists/{id}");
    public Task<List<TrackDto>?> ArtistTracksAsync(Guid id) => GetAsync<List<TrackDto>>($"api/artists/{id}/tracks");
    public Task<List<TrackDto>?> ArtistAppearancesAsync(Guid id) => GetAsync<List<TrackDto>>($"api/artists/{id}/appearances");
    public Task<List<TrackDto>?> ArtistTopTracksAsync(Guid id, int limit) => GetAsync<List<TrackDto>>($"api/artists/{id}/top?limit={limit}");
    public Task<PlaybackPreferences?> PlaybackPreferencesAsync() => GetAsync<PlaybackPreferences>("api/me/playback");
    public Task<PlaybackPreferences?> SavePlaybackPreferencesAsync(PlaybackPreferences prefs) => SendAsync<PlaybackPreferences>(HttpMethod.Put, "api/me/playback", prefs);
    public Task<List<TrackDto>?> RadioAsync(RadioRequest request) => SendAsync<List<TrackDto>>(HttpMethod.Post, "api/radio", request);
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
    public Task AdminReindexAsync() => SendAsync(HttpMethod.Post, "api/admin/search/reindex");
    public Task<PagedResult<TrackMetadataDto>?> AdminTracksAsync(string? q, Guid? albumId, int offset, int limit) =>
        GetAsync<PagedResult<TrackMetadataDto>>($"api/admin/tracks?offset={offset}&limit={limit}{Q(q)}{(albumId is { } a ? $"&albumId={a}" : "")}");
    public Task<TrackMetadataDto?> AdminUpdateTrackAsync(Guid id, UpdateTrackMetadataRequest req) =>
        SendAsync<TrackMetadataDto>(HttpMethod.Put, $"api/admin/tracks/{id}/metadata", req);
    public Task AdminDeleteTrackAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/tracks/{id}");
    public Task<AlbumDto?> AdminUpdateAlbumAsync(Guid id, UpdateAlbumRequest req) => SendAsync<AlbumDto>(HttpMethod.Put, $"api/admin/albums/{id}", req);
    public Task<AlbumDto?> AdminLinkAlbumReleaseAsync(Guid id, LinkAlbumReleaseRequest req) => SendAsync<AlbumDto>(HttpMethod.Put, $"api/admin/albums/{id}/musicbrainz", req);
    public Task AdminSetAlbumCoverAsync(Guid id, Stream image, string contentType) =>
        SendRawAsync(HttpMethod.Put, $"api/admin/albums/{id}/cover", image, contentType);
    public Task<UploadSessionDto?> AdminStartUploadAsync(StartUploadRequest req) => SendAsync<UploadSessionDto>(HttpMethod.Post, "api/admin/uploads", req);
    public Task<UploadSessionDto?> AdminUploadStatusAsync(Guid id) => GetAsync<UploadSessionDto>($"api/admin/uploads/{id}");
    public Task<UploadSessionDto?> AdminUploadChunkAsync(Guid id, long offset, ReadOnlyMemory<byte> data) =>
        SendRawAsync<UploadSessionDto>(HttpMethod.Put, $"api/admin/uploads/{id}?offset={offset}", new ReadOnlyMemoryContent(data), "application/octet-stream");
    public Task<List<TrackDto>?> AdminCompleteUploadAsync(Guid id) => SendAsync<List<TrackDto>>(HttpMethod.Post, $"api/admin/uploads/{id}/complete");
    public Task AdminCancelUploadAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/uploads/{id}");
    // ---- Demandes de téléchargement (contributeurs ; liste et décision : admin) ----
    public Task<YoutubeListingDto?> RequestSearchAsync(string q, bool music) => GetAsync<YoutubeListingDto>($"api/requests/search?music={music}&q={Uri.EscapeDataString(q)}");
    public Task<DownloadRequestDto?> CreateRequestAsync(CreateDownloadRequest req) => SendAsync<DownloadRequestDto>(HttpMethod.Post, "api/requests", req);
    public Task<PagedResult<DownloadRequestDto>?> MyRequestsAsync(int offset, int limit) => GetAsync<PagedResult<DownloadRequestDto>>($"api/requests/mine?offset={offset}&limit={limit}");
    public Task<RequestQuotaDto?> RequestQuotaAsync() => GetAsync<RequestQuotaDto>("api/requests/quota");
    public Task<PagedResult<DownloadRequestDto>?> AdminRequestsAsync(string? status, string? q, int offset, int limit) =>
        GetAsync<PagedResult<DownloadRequestDto>>($"api/admin/requests?offset={offset}&limit={limit}{(string.IsNullOrEmpty(status) ? "" : $"&status={status}")}{Q(q)}");
    public Task<DownloadRequestDto?> AdminApproveRequestAsync(Guid id) => SendAsync<DownloadRequestDto>(HttpMethod.Post, $"api/admin/requests/{id}/approve");
    public Task<DownloadRequestDto?> AdminDenyRequestAsync(Guid id, string? reason) => SendAsync<DownloadRequestDto>(HttpMethod.Post, $"api/admin/requests/{id}/deny", new DenyDownloadRequest(reason));

    public Task<List<UserDto>?> AdminUsersAsync() => GetAsync<List<UserDto>>("api/admin/users");
    public Task<UserDto?> AdminCreateUserAsync(CreateUserRequest req) => SendAsync<UserDto>(HttpMethod.Post, "api/admin/users", req);
    public Task<UserDto?> AdminUpdateUserAsync(Guid id, UpdateUserRequest req) => SendAsync<UserDto>(HttpMethod.Patch, $"api/admin/users/{id}", req);
    public Task AdminDeleteUserAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/users/{id}");
    public Task<DashboardDto?> AdminDashboardAsync(string range) =>
        GetAsync<DashboardDto>($"api/admin/dashboard?range={range}&offsetMinutes={(int)DateTimeOffset.Now.Offset.TotalMinutes}");

    // ---- Clés d'extension de navigateur (admin) ----
    public Task<List<CrashReportSummaryDto>?> CrashReportsAsync() => GetAsync<List<CrashReportSummaryDto>>("api/admin/crash-reports");
    public Task<CrashReportDto?> CrashReportAsync(Guid id) => GetAsync<CrashReportDto>($"api/admin/crash-reports/{id}");
    public Task DeleteCrashReportAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/crash-reports/{id}");
    public Task SendCrashReportAsync(CrashReportRequest report) => SendAsync(HttpMethod.Post, "api/crash-reports", report);
    public Task<List<ExtensionKeyDto>?> ExtensionKeysAsync() => GetAsync<List<ExtensionKeyDto>>("api/admin/extension-keys");
    public Task<CreatedExtensionKeyDto?> CreateExtensionKeyAsync(string name) =>
        SendAsync<CreatedExtensionKeyDto>(HttpMethod.Post, "api/admin/extension-keys", new CreateExtensionKeyRequest(name));
    public Task RevokeExtensionKeyAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/admin/extension-keys/{id}");

    // ---- YouTube (admin) ----
    private const string Yt = "api/admin/youtube";
    public Task<YoutubeStatusDto?> YoutubeStatusAsync(bool refresh = false) => GetAsync<YoutubeStatusDto>($"{Yt}/status?refresh={refresh}");
    public Task YoutubeResumeAsync() => SendAsync(HttpMethod.Post, $"{Yt}/resume");
    public Task<YoutubeSettingsDto?> YoutubeSettingsAsync() => GetAsync<YoutubeSettingsDto>($"{Yt}/settings");
    public Task<YoutubeSettingsDto?> YoutubeSaveSettingsAsync(YoutubeSettingsDto s) => SendAsync<YoutubeSettingsDto>(HttpMethod.Put, $"{Yt}/settings", s);
    public Task YoutubeSaveCookiesAsync(string content) => SendAsync(HttpMethod.Put, $"{Yt}/cookies", new YoutubeCookiesRequest(content));
    public Task YoutubeDeleteCookiesAsync() => SendAsync(HttpMethod.Delete, $"{Yt}/cookies");
    public Task<ToolsStatusDto?> YoutubeUpdateToolsAsync(bool deno) => SendAsync<ToolsStatusDto>(HttpMethod.Post, $"{Yt}/tools/update?deno={deno}");
    public Task<YoutubeListingDto?> YoutubeSearchAsync(string q, bool music) => GetAsync<YoutubeListingDto>($"{Yt}/search?music={music}&q={Uri.EscapeDataString(q)}");
    public Task<YoutubeListingTaskDto?> YoutubeStartListingAsync(string url) =>
        SendAsync<YoutubeListingTaskDto>(HttpMethod.Post, $"{Yt}/listings", new YoutubeListingStartRequest(url));
    public Task<YoutubeListingTaskDto?> YoutubeListingAsync(Guid id) => GetAsync<YoutubeListingTaskDto>($"{Yt}/listings/{id}");
    public Task<YoutubeImportResultDto?> YoutubeImportAsync(YoutubeImportRequest req) => SendAsync<YoutubeImportResultDto>(HttpMethod.Post, $"{Yt}/imports", req);
    public Task<PagedResult<YoutubeItemDto>?> YoutubeItemsAsync(string? status, bool review, string? q, int offset, int limit) =>
        GetAsync<PagedResult<YoutubeItemDto>>($"{Yt}/items?offset={offset}&limit={limit}&review={review}{(status is null ? "" : $"&status={status}")}{Q(q)}");
    public Task YoutubeRetryAsync(Guid id, string? format = null) =>
        SendAsync(HttpMethod.Post, $"{Yt}/items/{id}/retry{(string.IsNullOrWhiteSpace(format) ? "" : $"?format={Uri.EscapeDataString(format.Trim())}")}");
    public async Task<string?> YoutubeFormatsAsync(Guid id)
    {
        using var response = await SendCoreAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Yt}/items/{id}/formats"));
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStringAsync();
    }
    public Task YoutubeRetryFailedAsync() => SendAsync(HttpMethod.Post, $"{Yt}/items/retry-failed");
    public Task YoutubeDeleteItemAsync(Guid id, bool deleteTrack) => SendAsync(HttpMethod.Delete, $"{Yt}/items/{id}?deleteTrack={deleteTrack}");
    public Task<List<MetadataCandidateDto>?> YoutubeCandidatesAsync(Guid id) => GetAsync<List<MetadataCandidateDto>>($"{Yt}/items/{id}/candidates");
    public Task YoutubeApplyAsync(Guid id, MetadataCandidateDto c) => SendAsync(HttpMethod.Post, $"{Yt}/items/{id}/apply", c);
    public Task YoutubeAcceptAsync(Guid id) => SendAsync(HttpMethod.Post, $"{Yt}/items/{id}/accept");
    public Task<List<MetadataCandidateDto>?> YoutubeMusicBrainzAsync(string title, string? artist, double? duration) =>
        GetAsync<List<MetadataCandidateDto>>($"{Yt}/musicbrainz?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist ?? "")}{(duration is { } d ? $"&duration={d.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "")}");
    public Task<List<YoutubeSubscriptionDto>?> YoutubeSubscriptionsAsync() => GetAsync<List<YoutubeSubscriptionDto>>($"{Yt}/subscriptions");
    public Task YoutubeSubscribeAsync(CreateSubscriptionRequest req) => SendAsync(HttpMethod.Post, $"{Yt}/subscriptions", req);
    public Task YoutubeUpdateSubscriptionAsync(Guid id, UpdateSubscriptionRequest req) => SendAsync(HttpMethod.Put, $"{Yt}/subscriptions/{id}", req);
    public Task YoutubeDeleteSubscriptionAsync(Guid id) => SendAsync(HttpMethod.Delete, $"{Yt}/subscriptions/{id}");
    public Task YoutubeSyncSubscriptionAsync(Guid id) => SendAsync(HttpMethod.Post, $"{Yt}/subscriptions/{id}/sync");

    // ---- Tâches ----

    private const string Jobs = "api/admin/jobs";
    public Task<JobsOverviewDto?> JobsAsync(string? status, string? q, int offset, int limit) =>
        GetAsync<JobsOverviewDto>($"{Jobs}?offset={offset}&limit={limit}{(status is null ? "" : $"&status={status}")}{Q(q)}");
    public Task<PagedResult<JobDto>?> JobSuiteAsync(Guid id, int offset, int limit) => GetAsync<PagedResult<JobDto>>($"{Jobs}/suites/{id}?offset={offset}&limit={limit}");
    public Task<JobDetailDto?> JobAsync(Guid id) => GetAsync<JobDetailDto>($"{Jobs}/{id}");
    public Task<List<JobLogDto>?> JobLogsAsync(Guid id, long after) => GetAsync<List<JobLogDto>>($"{Jobs}/{id}/logs?after={after}");
    public Task<Guid> CreateJobAsync(CreateJobRequest req) => SendAsync<Guid>(HttpMethod.Post, Jobs, req);
    public Task CancelJobAsync(Guid id) => SendAsync(HttpMethod.Post, $"{Jobs}/{id}/cancel");
    public Task RetryJobAsync(Guid id) => SendAsync(HttpMethod.Post, $"{Jobs}/{id}/retry");
    public Task CancelJobSuiteAsync(Guid id) => SendAsync(HttpMethod.Post, $"{Jobs}/suites/{id}/cancel");
    public Task DeleteJobSuiteAsync(Guid id) => SendAsync(HttpMethod.Delete, $"{Jobs}/suites/{id}");

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

    /// <summary>Extrait de 30 s d'une vidéo YouTube (contributeurs), lu par une balise audio : jeton média dans l'adresse.</summary>
    public async Task<string?> PreviewUrlAsync(string videoId, double? durationSeconds)
    {
        var token = await auth.GetMediaTokenAsync();
        if (token is null || endpoint.BaseAddress is null) return null;
        var duration = durationSeconds is > 0 ? $"d={Math.Round(durationSeconds.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)}&" : "";
        return new Uri(endpoint.BaseAddress, $"api/requests/preview/{Uri.EscapeDataString(videoId)}?{duration}t={token}").ToString();
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
        var http = httpFactory.CreateClient("sonnuage");
        http.BaseAddress = endpoint.BaseAddress ?? throw new ApiException(0, Text("errors.serverNotConfigured"));

        var token = await auth.GetAccessTokenAsync() ?? throw new ApiException(HttpStatusCode.Unauthorized, Text("errors.sessionExpired"));
        var request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Localize(request);
        var response = await http.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized && retry)
        {
            response.Dispose();
            token = await auth.GetAccessTokenAsync(forceRefresh: true) ?? throw new ApiException(HttpStatusCode.Unauthorized, Text("errors.sessionExpired"));
            request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Localize(request);
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
            message = problem?.Detail ?? problem?.Title ?? response.ReasonPhrase ?? Text("errors.generic");
        }
        catch (Exception)
        {
            message = response.ReasonPhrase ?? Text("errors.http", ("status", (int)response.StatusCode));
        }
        throw new ApiException(response.StatusCode, message);
    }

    private sealed record ProblemPayload(string? Title, string? Detail);

    /// <summary>Le serveur répond (messages d'erreur) dans la langue de l'interface.</summary>
    internal static void Localize(HttpRequestMessage request)
    {
        if (Localizer.Current is { } l) request.Headers.AcceptLanguage.ParseAdd(l.Language);
    }

    private static string Text(string key, params (string, object?)[] args) =>
        Localizer.Current?[key, args] ?? SonNuage.Contracts.Localization.Translations.Instance.Get("en", key, args);
}
