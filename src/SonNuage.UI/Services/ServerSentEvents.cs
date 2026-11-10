using System.Runtime.CompilerServices;
using System.Text;

namespace SonNuage.UI.Services;

/// <summary>Un événement d'un flux <c>text/event-stream</c> : son nom (« message » par défaut) et ses données.</summary>
public sealed record ServerSentEvent(string Event, string Data);

/// <summary>
/// Lecture d'un flux Server-Sent Events. Les commentaires (battements de cœur « : ping ») ne produisent rien mais
/// prouvent que la connexion vit : sans aucune ligne pendant <c>idle</c>, le flux est tenu pour perdu (TimeoutException).
/// </summary>
public static class ServerSentEvents
{
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream, TimeSpan idle, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();
        var hasData = false;
        while (await ReadLineAsync(reader, idle, ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasData) yield return new ServerSentEvent(name ?? "message", data.ToString());
                (name, hasData) = (null, false);
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event":
                    name = value;
                    break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
            }
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, TimeSpan idle, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(idle);
        try
        {
            return await reader.ReadLineAsync(watchdog.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Flux d'événements muet.");
        }
    }
}
