using System.Text.Json;

namespace SonNuage.Server.Jam;

/// <summary>
/// État de lecture lu dans un « cluster » connect-state (réponse à l'enregistrement, ou mise à jour poussée par le
/// dealer). Une fois le Jam rejoint, c'est l'état de l'hôte : titre en cours, position et pause.
/// </summary>
/// <param name="TrackUri">URI Spotify du titre (<c>spotify:track:ID</c>), ou null si rien.</param>
/// <param name="PositionMs">Position connue à l'instant <see cref="TimestampUnixMs"/>.</param>
/// <param name="ServerTimestampMs">Horloge des serveurs Spotify à l'envoi (<c>server_timestamp_ms</c>), 0 si absente : sert
/// à estimer l'écart entre leur horloge et la nôtre (temps de vol compris).</param>
public sealed record ClusterState(string? TrackUri, string? Title, long PositionMs, long TimestampUnixMs, bool IsPaused, long DurationMs,
    IReadOnlyList<string>? NextTrackUris = null, long ServerTimestampMs = 0)
{
    public string? TrackId => TrackUri is { } uri && uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri["spotify:track:".Length..] : null;

    /// <summary>Position extrapolée à maintenant (si en lecture), en secondes.</summary>
    /// <param name="spotifyClockOffsetMs">Notre horloge moins celle de Spotify (les horodatages de l'état sont dans la leur).</param>
    public double PositionSecondsAt(DateTimeOffset now, long spotifyClockOffsetMs = 0)
    {
        var pos = PositionMs;
        if (!IsPaused && TimestampUnixMs > 0)
            pos += Math.Max(0, now.ToUnixTimeMilliseconds() - spotifyClockOffsetMs - TimestampUnixMs);
        if (DurationMs > 0) pos = Math.Min(pos, DurationMs);
        return Math.Max(0, pos) / 1000.0;
    }

    /// <summary>Réponse à l'enregistrement connect-state : un objet avec <c>player_state</c>.</summary>
    public static ClusterState? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromCluster(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mise à jour poussée par le dealer sur <c>hm://connect-state/v1/cluster</c> : JSON <c>{"cluster":{"player_state":…}}</c>
    /// pour un appareil enregistré en JSON (notre cas), protobuf <c>ClusterUpdate</c> sinon.
    /// </summary>
    public static ClusterState? ParseClusterUpdate(byte[] payload)
    {
        if (payload.Length == 0) return null;
        if (payload[0] != (byte)'{') return ParseClusterProto(payload);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return FromCluster(root.TryGetProperty("cluster", out var c) ? c : root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClusterState? FromCluster(JsonElement cluster)
    {
        if (cluster.ValueKind != JsonValueKind.Object || !cluster.TryGetProperty("player_state", out var ps) || FromPlayerState(ps) is not { } state) return null;
        return state with { ServerTimestampMs = Num(cluster, "server_timestamp_ms") };
    }

    private static ClusterState? FromPlayerState(JsonElement ps)
    {
        if (ps.ValueKind != JsonValueKind.Object) return null;
        string? uri = null, title = null;
        if (ps.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            uri = Text(t, "uri");
            if (t.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object) title = Text(md, "title");
        }
        var position = Num(ps, "position_as_of_timestamp");
        var timestamp = Num(ps, "timestamp");
        var duration = Num(ps, "duration");
        var paused = ps.TryGetProperty("is_paused", out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.String && p.GetString() == "true");
        // Sans URI de titre, l'état n'apporte rien.
        if (uri is null) return null;
        var next = new List<string>();
        if (ps.TryGetProperty("next_tracks", out var nt) && nt.ValueKind == JsonValueKind.Array)
            foreach (var t2 in nt.EnumerateArray())
                if (t2.ValueKind == JsonValueKind.Object && Text(t2, "uri") is { } u2 && u2.StartsWith("spotify:track:", StringComparison.Ordinal))
                    next.Add(u2);
        return new ClusterState(uri, title, position, timestamp, paused, duration, next);
    }

    /// <summary>Chaîne, ou null si absente ou d'un autre type (un champ inattendu ne doit pas couper le flux).</summary>
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Nombre éventuellement encodé en chaîne (les horodatages le sont souvent).</summary>
    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out var n) ? n : 0,
            _ => 0,
        };
    }

    // ---- Décodage des mises à jour poussées par le dealer (protobuf, pas JSON) ----
    // Le dealer envoie `hm://connect-state/v1/cluster` en protobuf `ClusterUpdate` (voir go-librespot). On ne lit que
    // les quelques champs utiles (uri, position, horodatage, pause, durée, file), sans dépendance protobuf lourde.
    // ClusterUpdate{ Cluster cluster=1 } · Cluster{ active_device_id=2; PlayerState player_state=3 }
    // PlayerState{ timestamp=1; ProvidedTrack track=7; position_as_of_timestamp=10; duration=11; is_paused=13; repeated ProvidedTrack next_tracks=20 }
    // ProvidedTrack{ string uri=1 }

    /// <summary>Décode un push cluster protobuf en état de lecture, ou null si rien d'exploitable.</summary>
    public static ClusterState? ParseClusterProto(byte[] data)
    {
        try
        {
            if (Field(data, 1) is not { } cluster) return null;     // ClusterUpdate.cluster
            if (Field(cluster, 3) is not { } playerState) return null; // Cluster.player_state
            long serverTs = 0;
            foreach (var f in Fields(cluster)) if (f.Field == 9 && f.Wire == 0) serverTs = f.Varint; // Cluster.server_timestamp_ms
            return FromPlayerStateProto(playerState) is { } s ? s with { ServerTimestampMs = serverTs } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ClusterState? FromPlayerStateProto(byte[] ps)
    {
        long timestamp = 0, position = 0, duration = 0;
        var paused = false;
        string? uri = null;
        var next = new List<string>();
        foreach (var f in Fields(ps))
        {
            switch (f.Field)
            {
                case 1: timestamp = f.Varint; break;
                case 10: position = f.Varint; break;
                case 11: duration = f.Varint; break;
                case 13: paused = f.Varint != 0; break;
                case 7 when f.Bytes is not null: uri = TrackUriProto(f.Bytes); break;
                case 20 when f.Bytes is not null: if (TrackUriProto(f.Bytes) is { } u) next.Add(u); break;
            }
        }
        return uri is null ? null : new ClusterState(uri, null, position, timestamp, paused, duration, next);
    }

    /// <summary>ProvidedTrack.uri (champ 1).</summary>
    private static string? TrackUriProto(byte[] track)
    {
        foreach (var f in Fields(track))
            if (f.Field == 1 && f.Bytes is not null) return System.Text.Encoding.UTF8.GetString(f.Bytes);
        return null;
    }

    private readonly record struct PbField(int Field, int Wire, byte[]? Bytes, long Varint);

    private static IEnumerable<PbField> Fields(byte[] data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var tag = ReadVarint(data, ref i);
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0: yield return new PbField(field, 0, null, ReadVarint(data, ref i)); break;
                case 1: i += 8; yield return new PbField(field, 1, null, 0); break;
                case 2:
                    var len = (int)ReadVarint(data, ref i);
                    if (len < 0 || i + len > data.Length) yield break;
                    var b = new byte[len];
                    Array.Copy(data, i, b, 0, len);
                    i += len;
                    yield return new PbField(field, 2, b, 0);
                    break;
                case 5: i += 4; yield return new PbField(field, 5, null, 0); break;
                default: yield break;
            }
        }
    }

    private static long ReadVarint(byte[] data, ref int i)
    {
        long result = 0;
        var shift = 0;
        while (i < data.Length && shift < 64)
        {
            var b = data[i++];
            result |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static byte[]? Field(byte[] data, int field)
    {
        foreach (var f in Fields(data))
            if (f.Field == field && f.Wire == 2) return f.Bytes;
        return null;
    }
}
