namespace SonNuage.Contracts;

/// <summary>Qualités de lecture proposées. <see cref="Original"/> sert le fichier tel quel.</summary>
public enum StreamQuality
{
    Original,
    Lossless,
    High,
    Normal,
    Low,
    DataSaver,
}

public static class StreamQualities
{
    public static string ToWire(this StreamQuality q) => q switch
    {
        StreamQuality.Original => "original",
        StreamQuality.Lossless => "lossless",
        StreamQuality.High => "high",
        StreamQuality.Normal => "normal",
        StreamQuality.Low => "low",
        StreamQuality.DataSaver => "data-saver",
        _ => throw new ArgumentOutOfRangeException(nameof(q)),
    };

    public static bool TryParse(string? value, out StreamQuality quality)
    {
        quality = StreamQuality.Original;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "original": return true;
            case "lossless": quality = StreamQuality.Lossless; return true;
            case "high": quality = StreamQuality.High; return true;
            case "normal": quality = StreamQuality.Normal; return true;
            case "low": quality = StreamQuality.Low; return true;
            case "data-saver" or "datasaver": quality = StreamQuality.DataSaver; return true;
            default: return false;
        }
    }

    public static string Label(this StreamQuality q) => q switch
    {
        StreamQuality.Original => "Originale",
        StreamQuality.Lossless => "Sans perte (FLAC)",
        StreamQuality.High => "Très haute (320 kbit/s)",
        StreamQuality.Normal => "Normale (192 kbit/s)",
        StreamQuality.Low => "Basse (128 kbit/s)",
        StreamQuality.DataSaver => "Économie de données (64 kbit/s)",
        _ => q.ToString(),
    };
}
