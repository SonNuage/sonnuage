using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

/// <summary>
/// Appareils du compte : relais de lecture entre ses lecteurs SonNuage (<see cref="PlaybackRelay"/>) et vue de ses
/// applis Subsonic (en cours d'écoute, file enregistrée).
/// </summary>
public static partial class DeviceEndpoints
{
    internal static TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex IdPattern();

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/" + DeviceLink.Route).RequireAuthorization().WithTags("Devices");

        g.MapGet("/", async (HttpContext http, PlaybackRelay relay, Subsonic.NowPlayingBoard board, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var playing = board.Current().Where(e => e.UserId == userId).ToList();
            var tracks = (await LibraryEndpoints.TracksInOrderAsync(db, userId, playing.Select(e => e.TrackId).ToList(), ct)).ToDictionary(t => t.Id);
            var apps = playing.Where(e => tracks.ContainsKey(e.TrackId))
                .Select(e => new SubsonicAppDto(e.Client ?? "Subsonic", tracks[e.TrackId], e.StartedAt)).ToList();
            return new DevicesDto(relay.Devices(userId), apps, await SavedQueueAsync(db, userId, ct));
        });

        g.MapGet("/link", (HttpContext http, string? id, string? name, string? platform, PlaybackRelay relay) =>
        {
            if (id is null || !IdPattern().IsMatch(id)) return Results.BadRequest();
            var device = new DeviceDto(id, Clean(name, DeviceLink.MaxNameLength) ?? id, Clean(platform, DeviceLink.MaxPlatformLength) ?? "");
            var userId = http.User.UserId();
            if (relay.Join(userId, device) is not { } connection) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            return new StreamResult(userId, connection, relay);
        });

        g.MapPost("/link", async (HttpContext http, PlaybackRelay relay) =>
        {
            if (http.Request.ContentLength > DeviceLink.MaxMessageBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            JsonObject? message;
            try
            {
                message = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            if (message is null
                || Text(message, "type") is not { } type || !DeviceLink.MessageTypes.Contains(type)
                || Text(message, "from") is not { } from
                || message["to"] is not null && Text(message, "to") is null)
                return Results.BadRequest();
            return relay.Relay(http.User.UserId(), from, Text(message, "to"), message) ? Results.NoContent() : Results.Conflict();
        });
    }

    private static string? Text(JsonObject message, string name) =>
        message[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length is > 0 and <= DeviceLink.MaxIdLength ? s : null;

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static async Task<SavedQueueDto?> SavedQueueAsync(SonNuageDbContext db, Guid userId, CancellationToken ct)
    {
        var queue = await db.SavedQueues.AsNoTracking().FirstOrDefaultAsync(q => q.UserId == userId, ct);
        if (queue is null) return null;
        var ids = await db.SavedQueueEntries.Where(e => e.UserId == userId).OrderBy(e => e.Position).Select(e => e.TrackId).ToListAsync(ct);
        var tracks = await LibraryEndpoints.TracksInOrderAsync(db, userId, ids, ct);
        if (tracks.Count == 0) return null;
        var current = queue.CurrentTrackId is { } c ? tracks.FindIndex(t => t.Id == c) : 0;
        return new SavedQueueDto(queue.ChangedBy, queue.ChangedAt, tracks, Math.Max(0, current), queue.PositionMs / 1000.0);
    }

    /// <summary>Le flux d'un appareil : « ready », puis les messages des autres, un battement de cœur, et « bye » à la fin.</summary>
    private sealed class StreamResult(Guid userId, PlaybackRelay.Connection connection, PlaybackRelay relay) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            var ct = http.RequestAborted;
            var response = http.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var reader = connection.Outbox.Reader;
            try
            {
                await response.WriteAsync($"event: {DeviceLink.ReadyEvent}\ndata: {{}}\n\n", ct);
                await response.Body.FlushAsync(ct);
                while (true)
                {
                    using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    beat.CancelAfter(Heartbeat);
                    bool more;
                    try
                    {
                        more = await reader.WaitToReadAsync(beat.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await response.WriteAsync(": ping\n\n", ct);
                        await response.Body.FlushAsync(ct);
                        continue;
                    }
                    // Boîte fermée : le même appareil s'est reconnecté, ce flux-ci s'arrête.
                    if (!more) break;
                    while (reader.TryRead(out var json)) await response.WriteAsync($"event: {DeviceLink.MessageEvent}\ndata: {json}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Appareil parti.
            }
            finally
            {
                relay.Leave(userId, connection);
            }
        }
    }
}
