using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Relais de lecture entre les appareils d'un compte, et ce que le compte voit de ses applis Subsonic.</summary>
public sealed class DeviceLinkTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_reaches_the_other_devices_of_the_account_with_the_sender_name()
    {
        var admin = await factory.AdminClientAsync();
        await using var phone = await Device.OpenAsync(admin, "phone-1", "Pixel de Phil", "android");
        await using var pc = await Device.OpenAsync(admin, "pc-1", "Bureau", "web");
        var other = await OtherUserAsync("neighbour");
        await using var neighbour = await Device.OpenAsync(other, "n-1", "Voisin", "web");

        await phone.SendAsync("""{"type":"claim","from":"phone-1"}""");

        var received = await pc.NextAsync();
        Assert.Equal("claim", received["type"]!.GetValue<string>());
        Assert.Equal("phone-1", received["from"]!.GetValue<string>());
        Assert.Equal("Pixel de Phil", received["fromName"]!.GetValue<string>());
        // Ni l'expéditeur ni un autre compte ne le reçoivent.
        await phone.NothingAsync();
        await neighbour.NothingAsync();
    }

    [Fact]
    public async Task A_message_to_one_device_reaches_only_it()
    {
        var admin = await factory.AdminClientAsync();
        await using var a = await Device.OpenAsync(admin, "to-a", "A", "web");
        await using var b = await Device.OpenAsync(admin, "to-b", "B", "web");
        await using var c = await Device.OpenAsync(admin, "to-c", "C", "web");

        await a.SendAsync("""{"type":"command","from":"to-a","to":"to-c","command":{"action":"next"}}""");

        Assert.Equal("next", (await c.NextAsync())["command"]!["action"]!.GetValue<string>());
        await b.NothingAsync();
    }

    [Fact]
    public async Task A_device_that_goes_away_says_goodbye()
    {
        var admin = await factory.AdminClientAsync();
        await using var stays = await Device.OpenAsync(admin, "bye-stays", "Reste", "web");
        var leaves = await Device.OpenAsync(admin, "bye-leaves", "Part", "android");

        await leaves.DisposeAsync();

        var bye = await stays.NextAsync();
        Assert.Equal(("bye", "bye-leaves"), (bye["type"]!.GetValue<string>(), bye["from"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Only_a_connected_device_can_send_and_only_known_messages()
    {
        var admin = await factory.AdminClientAsync();
        var unknown = await admin.PostAsync("/" + DeviceLink.LinkRoute, Json("""{"type":"claim","from":"ghost"}"""), Ct);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        await using var a = await Device.OpenAsync(admin, "valid-a", "A", "web");
        var odd = await admin.PostAsync("/" + DeviceLink.LinkRoute, Json("""{"type":"format-disk","from":"valid-a"}"""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);

        var badId = await admin.GetAsync($"/{DeviceLink.LinkRoute}?id=../x&name=A&platform=web", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        var anonymous = await factory.CreateClient().GetAsync($"/{DeviceLink.LinkRoute}?id=x&name=A&platform=web", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_account_sees_its_devices_its_subsonic_apps_and_their_saved_queue()
    {
        var user = await OtherUserAsync("listener");
        await using var phone = await Device.OpenAsync(user, "list-phone", "  Pixel de Phil  ", "android");
        var ids = await SubsonicAsync(user, "search3", "query=&artistCount=0&albumCount=0&songCount=50");
        var songs = ids.GetProperty("searchResult3").GetProperty("song").EnumerateArray()
            .ToDictionary(s => s.GetProperty("title").GetString()!, s => s.GetProperty("id").GetString()!);
        await SubsonicAsync(user, "scrobble", $"id={songs["Encore"]}&submission=false");
        await SubsonicAsync(user, "savePlayQueue", $"id={songs["Opening"]}&id={songs["Encore"]}&current={songs["Encore"]}&position=1500");

        var devices = (await user.GetFromJsonAsync<DevicesDto>("/" + DeviceLink.Route, Ct))!;

        var device = Assert.Single(devices.Devices);
        Assert.Equal(new DeviceDto("list-phone", "Pixel de Phil", "android"), device);
        var app = Assert.Single(devices.Apps);
        Assert.Equal(("Symfonium", "Encore"), (app.Client, app.Track.Title));
        var queue = devices.SavedQueue!;
        Assert.Equal(["Opening", "Encore"], queue.Tracks.Select(t => t.Title));
        Assert.Equal((1, 1.5, "Symfonium"), (queue.CurrentIndex, queue.PositionSeconds, queue.ChangedBy));
    }

    private async Task<HttpClient> OtherUserAsync(string name)
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(name, name + "-password", Roles.User), Ct);
        return await factory.ClientAsync(name, name + "-password");
    }

    private async Task<JsonElement> SubsonicAsync(HttpClient account, string method, string query)
    {
        var access = (await (await account.PostAsync("/api/me/subsonic", null, Ct)).Content.ReadFromJsonAsync<SubsonicAccessDto>(Ct))!;
        var text = await factory.CreateClient().GetStringAsync($"/rest/{method}?apiKey={access.Password}&v=1.16.1&c=Symfonium&f=json&{query}", Ct);
        return JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").Clone();
    }

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    /// <summary>Un appareil relié : son flux est lu en tâche de fond, ses messages attendent dans une file.</summary>
    private sealed class Device : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly Channel<JsonObject> _inbox = Channel.CreateUnbounded<JsonObject>();
        private HttpResponseMessage? _response;
        private Task? _reader;

        private Device(HttpClient client) => _client = client;

        public static async Task<Device> OpenAsync(HttpClient client, string id, string name, string platform)
        {
            var device = new Device(client);
            var url = $"/{DeviceLink.LinkRoute}?id={id}&name={Uri.EscapeDataString(name)}&platform={platform}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/event-stream");
            device._response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.OK, device._response.StatusCode);
            var stream = await device._response.Content.ReadAsStreamAsync(Ct);
            var ready = new TaskCompletionSource();
            device._reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in ServerSentEvents.ReadAsync(stream, TimeSpan.FromSeconds(60), device._stop.Token))
                    {
                        if (e.Event == DeviceLink.ReadyEvent) ready.TrySetResult();
                        else if (e.Event == DeviceLink.MessageEvent) await device._inbox.Writer.WriteAsync(JsonNode.Parse(e.Data)!.AsObject());
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
                {
                }
            });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            return device;
        }

        public async Task SendAsync(string json)
        {
            var response = await _client.PostAsync("/" + DeviceLink.LinkRoute, Json(json), Ct);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public async Task<JsonObject> NextAsync() => await _inbox.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        public async Task NothingAsync()
        {
            await Task.Delay(300, Ct);
            Assert.False(_inbox.Reader.TryRead(out var unexpected), unexpected?.ToJsonString());
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _response?.Dispose();
            if (_reader is not null) await _reader;
        }
    }
}
