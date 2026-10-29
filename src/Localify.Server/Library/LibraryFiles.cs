using System.Text.RegularExpressions;

namespace Localify.Server.Library;

public static partial class LibraryFiles
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".alac", ".ape", ".wv",
    };

    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif", ".alac", ".ape", ".wv",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    /// <summary>Formats que tous les navigateurs et ExoPlayer lisent sans transcodage.</summary>
    private static readonly HashSet<string> UniversallyPlayable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus",
    };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));
    public static bool IsLossless(string path) => LosslessExtensions.Contains(Path.GetExtension(path));
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsDirectlyPlayable(string format) => UniversallyPlayable.Contains("." + format);

    public static string Format(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/ogg",
        ".m4a" or ".alac" => "audio/mp4",
        ".aac" => "audio/aac",
        ".wma" => "audio/x-ms-wma",
        ".aiff" or ".aif" => "audio/aiff",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

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
