using System.Net;
using System.Text;
using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>
/// Le relais vu d'un appareil : une montre dont l'écran s'éteint perd son flux sans le savoir, et ses commandes
/// suivantes ne doivent pas se perdre en silence.
/// </summary>
public sealed class ServerPlaybackLinkTests
{
    private static readonly Uri Server = new("https://music.example.org/");
    private static readonly PlaybackMessage Toggle = new("command", "watch", To: "phone", Command: new PlayerCommand("toggle"));

    [Fact]
    public async Task A_message_refused_because_the_server_forgot_the_device_is_sent_again_on_a_new_stream()
    {
        var relay = new FakeRelay { Refusals = 1 };
        using var link = await LinkAsync(relay);
        await link.StartAsync();

        await link.SendAsync(Toggle with { From = link.DeviceId });

        Assert.Equal(2, relay.Streams);
        Assert.Contains("\"toggle\"", Assert.Single(relay.Posted));
    }

    [Fact]
    public async Task A_message_sent_while_the_stream_reopens_waits_for_it()
    {
        var relay = new FakeRelay();
        using var link = await LinkAsync(relay);
        await link.StartAsync();

        relay.CloseStream();
        await relay.WaitUntilClosedAsync();
        await link.SendAsync(Toggle with { From = link.DeviceId });

        Assert.Single(relay.Posted);
        Assert.Equal(2, relay.Streams);
    }

    private static async Task<ServerPlaybackLink> LinkAsync(FakeRelay relay)
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        var user = new UserDto(Guid.NewGuid(), "alice", Roles.User, DateTimeOffset.UnixEpoch);
        var account = await accounts.SaveSignedInAsync(Server, user);
        store.Data[account.SessionKey] = JsonSerializer.Serialize(
            new TokenResponse("access", DateTimeOffset.UtcNow.AddHours(1), "refresh", "media", DateTimeOffset.UtcNow.AddDays(1), user),
            JsonDefaults.Options);
        await accounts.SwitchAsync(account.Id);
        var factory = new HandlerFactory(relay);
        var endpoint = new FixedEndpoint(Server);
        var auth = new AuthSession(store, endpoint, accounts, factory);
        await auth.EnsureLoadedAsync();
        return new ServerPlaybackLink(new ApiClient(factory, endpoint, auth), auth, accounts, new DeviceIdentity(store, new WatchPlatform()));
    }

    private sealed class WatchPlatform : IPlatformInfo
    {
        public bool HasMeteredNetworkDetection => false;
        public string Name => "wear";
    }

    /// <summary>
    /// Le serveur : un flux qui reste ouvert après « ready ». Comme le vrai, il refuse (409) le message d'un appareil
    /// dont le flux est fermé, et ici aussi tant qu'il reste des <see cref="Refusals"/> (flux perdu sans le savoir).
    /// </summary>
    private sealed class FakeRelay : HttpMessageHandler
    {
        private readonly object _lock = new();
        private OpenStream? _current;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Refusals { get; set; }
        public int Streams { get; private set; }
        public List<string> Posted { get; } = [];

        public void CloseStream()
        {
            lock (_lock)
            {
                _current?.Close(_closed);
                _current = null;
            }
        }

        public Task WaitUntilClosedAsync() => _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                var stream = new OpenStream();
                lock (_lock)
                {
                    Streams++;
                    _current = stream;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            }
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (_lock)
            {
                if (_current is null || Refusals > 0)
                {
                    if (_current is not null) Refusals--;
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                Posted.Add(body);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    /// <summary>« ready », puis plus rien jusqu'à la fermeture.</summary>
    private sealed class OpenStream : Stream
    {
        private readonly byte[] _ready = Encoding.UTF8.GetBytes($"event: {DeviceLink.ReadyEvent}\ndata: {{}}\n\n");
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        private TaskCompletionSource? _closed;

        public void Close(TaskCompletionSource closed)
        {
            _closed = closed;
            _end.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < _ready.Length)
            {
                var n = Math.Min(buffer.Length, _ready.Length - _position);
                _ready.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            await _end.Task.WaitAsync(ct);
            _closed?.TrySetResult();
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
