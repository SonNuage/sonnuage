using System.Text.RegularExpressions;

namespace SonNuage.Server.Library;

public static partial class LibraryFiles
{
    /// <summary>Ce qu'on sait d'une extension. Une seule table : ajouter un format, c'est ajouter une ligne.</summary>
    private sealed record Kind(string ContentType, bool Audio = false, bool Lossless = false, bool Playable = false, bool Image = false);

    private static readonly Dictionary<string, Kind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Playable : lu par tous les navigateurs et ExoPlayer sans transcodage.
        [".mp3"] = new("audio/mpeg", Audio: true, Playable: true),
        [".flac"] = new("audio/flac", Audio: true, Lossless: true, Playable: true),
        [".wav"] = new("audio/wav", Audio: true, Lossless: true, Playable: true),
        [".ogg"] = new("audio/ogg", Audio: true, Playable: true),
        [".oga"] = new("audio/ogg", Audio: true),
        [".opus"] = new("audio/ogg", Audio: true, Playable: true),
        [".m4a"] = new("audio/mp4", Audio: true, Playable: true),
        [".alac"] = new("audio/mp4", Audio: true, Lossless: true),
        [".aac"] = new("audio/aac", Audio: true, Playable: true),
        [".wma"] = new("audio/x-ms-wma", Audio: true),
        [".aiff"] = new("audio/aiff", Audio: true, Lossless: true),
        [".aif"] = new("audio/aiff", Audio: true, Lossless: true),
        [".ape"] = new(OctetStream, Audio: true, Lossless: true),
        [".wv"] = new(OctetStream, Audio: true, Lossless: true),
        [".jpg"] = new("image/jpeg", Image: true),
        [".jpeg"] = new("image/jpeg", Image: true),
        [".png"] = new("image/png", Image: true),
        [".webp"] = new("image/webp", Image: true),
    };

    private const string OctetStream = "application/octet-stream";
    private static readonly Kind Unknown = new(OctetStream);

    private static Kind KindOf(string path) => Kinds.GetValueOrDefault(Path.GetExtension(path), Unknown);

    public static bool IsAudio(string path) => KindOf(path).Audio;
    public static bool IsLossless(string path) => KindOf(path).Lossless;
    public static bool IsImage(string path) => KindOf(path).Image;
    public static bool IsDirectlyPlayable(string format) => KindOf("." + format).Playable;

    public static string Format(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static string ContentType(string path) => KindOf(path).ContentType;

    /// <summary>
    /// « Titre (1).mp3 » à côté de « Titre.mp3 » de taille quasi identique (les tags peuvent différer de
    /// quelques octets) : copie Windows en double, ignorée.
    /// </summary>
    public static bool IsDuplicateCopy(string path)
    {
        var name = Path.GetFileName(path);
        var m = CopySuffix().Match(name);
        if (!m.Success) return false;
        var original = Path.Combine(Path.GetDirectoryName(path)!, m.Groups[1].Value + m.Groups[2].Value);
        if (!File.Exists(original)) return false;
        try
        {
            var a = new FileInfo(original).Length;
            var b = new FileInfo(path).Length;
            return Math.Abs(a - b) <= Math.Max(a, b) / 100;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Score d'une image candidate comme pochette (plus haut = meilleur).</summary>
    public static int CoverRank(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (n is "cover" or "folder" or "front") return 100;
        if (n.StartsWith("albumart") && n.EndsWith("_large")) return 95;
        if (n is "albumart") return 92;
        if (n.Contains("front")) return 90;
        if (n.StartsWith("cover") || n.StartsWith("folder")) return 85;
        if (n.StartsWith("albumart") && !n.Contains("small")) return 80;
        if (n.Contains("back") || n.Contains("disc") || n.Contains("cd")) return 10;
        if (n.Contains("small")) return 20;
        return 50;
    }

    [GeneratedRegex(@"^(.*) \(\d+\)(\.[^.]+)$")]
    private static partial Regex CopySuffix();
}
