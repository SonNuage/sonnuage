using System.Text.Json;
using System.Text.Json.Nodes;

namespace SonNuage.Server.Analysis;

/// <summary>Lignes JSON écrites par le script d'analyse (essentia_analyzer.py), lues ici une fois pour toutes.</summary>
public static class EssentiaProtocol
{
    /// <summary>Tempo plausible ; au-delà, la valeur est écartée.</summary>
    private const double MaxBpm = 400;

    public abstract record Message;

    /// <summary>Modèles chargés : le script attend les fichiers.</summary>
    public sealed record Ready(int Version) : Message;

    /// <summary>Le script n'a pas pu démarrer (Essentia ou modèles absents).</summary>
    public sealed record Fatal(string Error) : Message;

    /// <summary>Résultat de la demande <paramref name="Id"/>.</summary>
    public sealed record Result(string? Id, AudioAnalysisOutcome Outcome) : Message;

    /// <summary>Tout le reste (journal égaré sur la sortie standard…) : ignoré.</summary>
    public sealed record Other : Message;

    public static readonly Message Noise = new Other();

    /// <summary>Demande d'analyse d'un fichier, sur une ligne.</summary>
    public static string Request(string id, string path) => JsonSerializer.Serialize(new { id, path });

    public static Message Parse(string line)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return Noise;
        }
        if (o is null) return Noise;
        if (o["ready"] is not null) return new Ready(Number(o["version"]) is { } v ? (int)v : 0);
        if (Text(o["fatal"]) is { } fatal) return new Fatal(fatal);
        var id = Text(o["id"]);
        if (Text(o["error"]) is { } error) return new Result(id, new AudioAnalysisOutcome.Failed(error));
        if (o["features"] is JsonObject f) return new Result(id, new AudioAnalysisOutcome.Analyzed(Features(f)));
        return Noise;
    }

    private static AudioFeatureValues Features(JsonObject f)
    {
        double? Unit(string name) => Number(f[name]) is { } v && v is >= 0 and <= 1 ? v : null;
        var bpm = Number(f["bpm"]) is { } b && b is > 0 and <= MaxBpm ? b : (double?)null;
        return new AudioFeatureValues(bpm, Unit("danceability"), Unit("energy"), Unit("valence"), Unit("happy"), Unit("sad"),
            Unit("relaxed"), Unit("aggressive"), Unit("party"), Unit("acoustic"), Unit("electronic"), Unit("instrumental"),
            Styles(f["styles"]));
    }

    /// <summary>Paires [libellé, activation] ; ce qui n'en est pas une est écarté.</summary>
    private static string? Styles(JsonNode? n) => n is JsonArray a
        ? DiscogsStyles.Encode(a.OfType<JsonArray>().Where(p => p.Count == 2 && Text(p[0]) is not null && Number(p[1]) is not null)
            .Select(p => (Text(p[0])!, Number(p[1])!.Value)))
        : null;

    private static double? Number(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
