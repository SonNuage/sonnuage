using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.ListenBrainz;

namespace SonNuage.Server.Tests;

/// <summary>ListenBrainz simulé : jamais le vrai service depuis les tests.</summary>
public sealed class FakeListenBrainz : HttpMessageHandler
{
    public sealed record Call(string Method, string Path, string? Token, string Body);

    public ConcurrentQueue<Call> Calls { get; } = new();
    /// <summary>Jetons acceptés, avec le nom ListenBrainz de leur propriétaire.</summary>
    public ConcurrentDictionary<string, string> Users { get; } = new();
    /// <summary>Réponse imposée à l'envoi d'écoutes, par jeton (sinon 200).</summary>
    public ConcurrentDictionary<string, Func<HttpResponseMessage>> SubmitAnswers { get; } = new();
    public bool Down { get; set; }
    public string RecommendationsJson { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";

    public IEnumerable<Call> By(string token) => Calls.Where(c => c.Token == token);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var auth = request.Headers.Authorization;
        var token = auth?.Scheme == "Token" ? auth.Parameter : null;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.PathAndQuery;
        Calls.Enqueue(new Call(request.Method.Method, path, token, body));
        if (Down) throw new HttpRequestException("ListenBrainz injoignable");
        if (path.StartsWith("/1/validate-token"))
            return Json(token is not null && Users.TryGetValue(token, out var name)
                ? $$"""{"code":200,"message":"Token valid.","valid":true,"user_name":"{{name}}"}"""
                : """{"code":200,"message":"Token invalid.","valid":false}""");
        if (path.StartsWith("/1/submit-listens"))
        {
            if (token is null || !Users.ContainsKey(token)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return SubmitAnswers.TryGetValue(token, out var answer) ? answer() : Json("""{"status":"ok"}""");
        }
        if (path.StartsWith("/1/cf/recommendation/user/"))
            return RecommendationsJson.Length == 0 ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(RecommendationsJson);
        if (path.StartsWith("/1/metadata/recording")) return Json(MetadataJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class ListenBrainzFactory : SonNuageFactory
{
    public FakeListenBrainz ListenBrainz { get; } = new();

    protected override void ConfigureMore(IWebHostBuilder builder)
    {
        builder.UseSetting("SonNuage:ListenBrainzUrl", "https://listenbrainz.test");
        builder.ConfigureServices(s => s.AddHttpClient(ListenBrainzApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ListenBrainz));
    }
}

/// <summary>
/// ListenBrainz, niveau 2 (facultatif, par compte) : jeton validé et chiffré, envoi des écoutes après accord,
/// recommandations personnelles.
/// </summary>
public sealed class ListenBrainzTests(ListenBrainzFactory factory) : IClassFixture<ListenBrainzFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FakeListenBrainz Lb => factory.ListenBrainz;

    private async Task<(HttpClient Client, Guid UserId, List<TrackDto> Tracks)> AccountAsync(string name, string role = Roles.User)
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var created = await (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, $"{name}-password", role), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<UserDto>(Ct);
        var client = await factory.ClientAsync(name, $"{name}-password");
        var artists = await client.GetFromJsonAsync<PagedResult<ArtistDto>>("/api/artists?limit=50", Ct);
        var tracks = new List<TrackDto>();
        foreach (var artist in artists!.Items)
            tracks.AddRange((await client.GetFromJsonAsync<List<TrackDto>>($"/api/artists/{artist.Id}/tracks", Ct))!);
        return (client, created!.Id, tracks);
    }

    /// <summary>Compte relié à ListenBrainz avec un jeton qui lui est propre (les appels se reconnaissent à leur jeton).</summary>
    private async Task<(HttpClient Client, Guid UserId, List<TrackDto> Tracks, string Token)> ConnectedAsync(string name, bool submit,
        string role = Roles.User)
    {
        var (client, userId, tracks) = await AccountAsync(name, role);
        var token = Guid.NewGuid().ToString();
        Lb.Users[token] = "lb-" + name;
        (await client.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest(token), Ct)).EnsureSuccessStatusCode();
        if (submit)
            (await client.PutAsJsonAsync("/api/me/listenbrainz/submit", new ListenBrainzSubmitRequest(true), Ct)).EnsureSuccessStatusCode();
        return (client, userId, tracks, token);
    }

    private async Task PlayedAsync(Guid userId, TrackDto track, DateTimeOffset at, double seconds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        db.PlayEvents.Add(new PlayEvent { UserId = userId, TrackId = track.Id, PlayedAt = at, SecondsPlayed = seconds, Completed = true, Client = "web" });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<T> InDbAsync<T>(Func<SonNuageDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>());
    }

    private Task<ListenBrainzSubmitter.Run> SubmitAsync() => factory.Services.GetRequiredService<ListenBrainzSubmitter>().SubmitPendingAsync(Ct);

    private static async Task<ListenBrainzAccountDto> AccountOf(HttpClient client) =>
        (await client.GetFromJsonAsync<ListenBrainzAccountDto>("/api/me/listenbrainz", Ct))!;

    // ---- Jeton et accord ----

    [Fact]
    public async Task A_valid_token_is_stored_encrypted_and_never_sent_back()
    {
        var (client, userId, _) = await AccountAsync("lb-store");
        var token = Guid.NewGuid().ToString();
        Lb.Users[token] = "alice-lb";

        var saved = await (await client.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest($"  {token} "), Ct))
            .EnsureSuccessStatusCode().Content.ReadAsStringAsync(Ct);

        Assert.Equal(("/1/validate-token", token), Lb.By(token).Select(c => (c.Path, c.Token)).Single());
        Assert.DoesNotContain(token, saved);
        var raw = await client.GetStringAsync("/api/me/listenbrainz", Ct);
        Assert.DoesNotContain(token, raw);
        Assert.Equal(new ListenBrainzAccountDto(true, "alice-lb", false, 0), await AccountOf(client));
        var stored = await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.ListenBrainzToken).SingleAsync(Ct));
        Assert.NotNull(stored);
        Assert.DoesNotContain(token, stored);
    }

    [Fact]
    public async Task A_token_ListenBrainz_refuses_or_cannot_check_is_not_kept()
    {
        var (client, _, _) = await AccountAsync("lb-refused");

        var refused = await client.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest("not-a-token"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Lb.Down = true;
        try
        {
            var down = await client.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest(Guid.NewGuid().ToString()), Ct);
            Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        }
        finally
        {
            Lb.Down = false;
        }
        var calls = Lb.Calls.Count;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest("   "), Ct)).StatusCode);
        Assert.Equal(calls, Lb.Calls.Count);   // vide : pas même demandé à ListenBrainz

        Assert.False((await AccountOf(client)).Connected);
    }

    [Fact]
    public async Task Sending_listens_is_off_by_default_needs_a_token_and_stops_with_its_removal()
    {
        var (client, _, _) = await AccountAsync("lb-optin");
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync("/api/me/listenbrainz/submit", new ListenBrainzSubmitRequest(true), Ct)).StatusCode);

        var token = Guid.NewGuid().ToString();
        Lb.Users[token] = "optin-lb";
        (await client.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest(token), Ct)).EnsureSuccessStatusCode();
        Assert.False((await AccountOf(client)).Submit);
        var on = await (await client.PutAsJsonAsync("/api/me/listenbrainz/submit", new ListenBrainzSubmitRequest(true), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ListenBrainzAccountDto>(Ct);
        Assert.True(on!.Submit);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me/listenbrainz/token", Ct)).StatusCode);
        Assert.Equal(new ListenBrainzAccountDto(false, null, false, 0), await AccountOf(client));
    }

    [Fact]
    public async Task Each_account_has_its_own_connection_and_a_session_is_needed()
    {
        await ConnectedAsync("lb-alice", submit: true);
        var (bob, _, _) = await AccountAsync("lb-bob");

        Assert.Equal(new ListenBrainzAccountDto(false, null, false, 0), await AccountOf(bob));
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/listenbrainz", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/me/listenbrainz/token", new ListenBrainzTokenRequest("x"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/listenbrainz/recommendations", Ct)).StatusCode);
    }

    // ---- Envoi des écoutes ----

    [Fact]
    public async Task Only_real_listens_made_after_opting_in_are_sent_once()
    {
        var (_, userId, tracks, token) = await ConnectedAsync("lb-send", submit: true);
        var opening = tracks.Single(t => t.Title == "Opening");
        var encore = tracks.Single(t => t.Title == "Encore");
        var mbid = Guid.NewGuid();
        await InDbAsync(db => db.Tracks.Where(t => t.Id == opening.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.MusicBrainzRecordingId, mbid), Ct));
        await InDbAsync(db => db.Tracks.Where(t => t.Id == encore.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.MusicBrainzRecordingId, (Guid?)null), Ct));
        var now = DateTimeOffset.UtcNow;
        await PlayedAsync(userId, encore, now.AddHours(-1), 1);         // avant l'accord : reste ici
        await PlayedAsync(userId, opening, now.AddSeconds(1), 2);       // tout le morceau
        await PlayedAsync(userId, encore, now.AddSeconds(2), 0.6);      // la moitié d'un morceau d'une seconde
        await PlayedAsync(userId, opening, now.AddSeconds(3), 0.4);     // passé trop tôt : pas une écoute

        var run = await SubmitAsync();

        var call = Assert.Single(Lb.By(token), c => c.Path.StartsWith("/1/submit-listens"));
        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("import", body.GetProperty("listen_type").GetString());
        var listens = body.GetProperty("payload").EnumerateArray().ToList();
        Assert.Equal(["Opening", "Encore"], listens.Select(l => l.GetProperty("track_metadata").GetProperty("track_name").GetString()));
        Assert.Equal((now.AddSeconds(1)).ToUnixTimeSeconds(), listens[0].GetProperty("listened_at").GetInt64());
        var first = listens[0].GetProperty("track_metadata");
        Assert.Equal("Test Artist", first.GetProperty("artist_name").GetString());
        Assert.Equal("First Album", first.GetProperty("release_name").GetString());
        Assert.Equal(mbid.ToString(), first.GetProperty("additional_info").GetProperty("recording_mbid").GetString());
        Assert.False(listens[1].GetProperty("track_metadata").GetProperty("additional_info").TryGetProperty("recording_mbid", out _));
        Assert.True(run.RetryAt is null);

        await SubmitAsync();
        Assert.Single(Lb.By(token), c => c.Path.StartsWith("/1/submit-listens"));
        Assert.Equal(2, (await AccountOf(await factory.ClientAsync("lb-send", "lb-send-password"))).Sent);
    }

    [Fact]
    public async Task One_listen_goes_as_a_single_listen()
    {
        var (_, userId, tracks, token) = await ConnectedAsync("lb-single", submit: true);
        await PlayedAsync(userId, tracks[0], DateTimeOffset.UtcNow.AddSeconds(1), 5);
        // Ce que la planification regarde avant de demander la tâche.
        Assert.True(await InDbAsync(db => ListenBrainzSubmitter.AnyPendingAsync(db, Ct)));

        await SubmitAsync();

        var call = Assert.Single(Lb.By(token), c => c.Path.StartsWith("/1/submit-listens"));
        Assert.Equal("single", JsonDocument.Parse(call.Body).RootElement.GetProperty("listen_type").GetString());
    }

    [Fact]
    public async Task Nothing_leaves_for_an_account_that_did_not_opt_in()
    {
        var (_, userId, tracks, token) = await ConnectedAsync("lb-silent", submit: false);
        await PlayedAsync(userId, tracks[0], DateTimeOffset.UtcNow.AddSeconds(1), 5);

        await SubmitAsync();

        Assert.DoesNotContain(Lb.By(token), c => c.Path.StartsWith("/1/submit-listens"));
    }

    [Fact]
    public async Task When_ListenBrainz_asks_to_slow_down_or_fails_the_listens_wait_for_a_later_try()
    {
        var (_, userId, tracks, token) = await ConnectedAsync("lb-later", submit: true);
        await PlayedAsync(userId, tracks[0], DateTimeOffset.UtcNow.AddSeconds(1), 5);
        Lb.SubmitAnswers[token] = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Reset-In", "42");
            return r;
        };

        var limited = await SubmitAsync();
        Assert.InRange(limited.RetryAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45));

        Lb.SubmitAnswers[token] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var failed = await SubmitAsync();
        Assert.NotNull(failed.RetryAt);

        Lb.SubmitAnswers.TryRemove(token, out _);
        Assert.Null((await SubmitAsync()).RetryAt);
        Assert.Equal(3, Lb.By(token).Count(c => c.Path.StartsWith("/1/submit-listens")));
        Assert.Equal(1, (await AccountOf(await factory.ClientAsync("lb-later", "lb-later-password"))).Sent);
    }

    [Fact]
    public async Task A_token_revoked_on_ListenBrainz_disconnects_the_account()
    {
        var (client, userId, tracks, token) = await ConnectedAsync("lb-revoked", submit: true);
        await PlayedAsync(userId, tracks[0], DateTimeOffset.UtcNow.AddSeconds(1), 5);
        Lb.Users.TryRemove(token, out _);

        await SubmitAsync();

        Assert.Equal(new ListenBrainzAccountDto(false, null, false, 0), await AccountOf(client));
    }

    // ---- Recommandations ----

    [Fact]
    public async Task Recommendations_in_the_library_are_offered_to_everyone_the_others_only_to_those_who_can_ask()
    {
        var (user, _, _, _) = await ConnectedAsync("lb-reco-user", submit: false);
        var (contributor, _, _, _) = await ConnectedAsync("lb-reco-contrib", submit: false, Roles.Contributor);
        var (stranger, _, _) = await AccountAsync("lb-reco-none");
        var opening = Guid.NewGuid();
        var encore = Guid.NewGuid();
        var far = Guid.NewGuid();
        await InDbAsync(db => db.Tracks.Where(t => t.Title == "Opening").ExecuteUpdateAsync(s => s.SetProperty(t => t.MusicBrainzRecordingId, opening), Ct));
        await InDbAsync(db => db.Tracks.Where(t => t.Title == "Encore").ExecuteUpdateAsync(s => s.SetProperty(t => t.MusicBrainzRecordingId, encore), Ct));
        Lb.RecommendationsJson = $$"""
            {"payload":{"count":3,"entity":"recording","mbids":[
              {"recording_mbid":"{{far}}","score":0.9},{"recording_mbid":"{{encore}}","score":0.8},{"recording_mbid":"{{opening}}","score":0.5}],
             "user_name":"lb-reco-user"}
            }
            """;
        Lb.MetadataJson = $$"""
            {"{{far}}":{"recording":{"name":"Far Song","length":200000},"artist":{"name":"Far Artist"},
                "release":{"name":"Far Away"}
              }
            }
            """;

        var forUser = (await user.GetFromJsonAsync<ListenBrainzRecommendationsDto>("/api/me/listenbrainz/recommendations", Ct))!;
        Assert.Equal(["Encore", "Opening"], forUser.Tracks.Select(t => t.Title));
        Assert.Empty(forUser.Outside);
        Assert.Contains(Lb.Calls, c => c.Path.StartsWith("/1/cf/recommendation/user/lb-lb-reco-user/recording"));

        var forContributor = (await contributor.GetFromJsonAsync<ListenBrainzRecommendationsDto>("/api/me/listenbrainz/recommendations", Ct))!;
        Assert.Equal([new ExternalRecordingDto(far.ToString(), "Far Song", "Far Artist", "Far Away")], forContributor.Outside);

        var calls = Lb.Calls.Count;
        var none = (await stranger.GetFromJsonAsync<ListenBrainzRecommendationsDto>("/api/me/listenbrainz/recommendations", Ct))!;
        Assert.Empty(none.Tracks);
        Assert.Equal(calls, Lb.Calls.Count);   // sans jeton enregistré, rien ne part vers ListenBrainz
    }
}
