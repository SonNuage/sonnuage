using SonNuage.Contracts.Localization;
using System.Text.Json;
using SonNuage.Contracts;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Youtube;

/// <summary>
/// Réglages de l'import YouTube modifiables depuis l'admin, et cookies youtube.com (format Netscape,
/// exporté du navigateur) qui évitent les « confirmez que vous n'êtes pas un robot ». Les deux fichiers
/// vivent dans le dossier de données, jamais servis.
/// </summary>
public sealed class YoutubeSettingsStore
{
    // Au-delà, YouTube repère vite un comportement de robot (vérification, 429).
    public const int MaxParallelDownloads = 6;

    public static readonly YoutubeSettingsDto Defaults = new(SyncIntervalHours: 6, AudioFormat: "original", AcoustIdKey: null, AutoAcceptThreshold: 0.8);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _settingsPath;
    private readonly string _cookiesPath;
    private readonly Lock _lock = new();
    private YoutubeSettingsDto _settings;

    public YoutubeSettingsStore(IOptions<SonNuageOptions> options)
    {
        var dir = options.Value.YoutubeDataPath;
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
        _cookiesPath = Path.Combine(dir, "cookies.txt");
        _settings = Defaults;
        try
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<YoutubeSettingsDto>(File.ReadAllText(_settingsPath), Json) ?? Defaults;
        }
        catch (JsonException)
        {
        }
    }

    public YoutubeSettingsDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    public event Action? Changed;

    public (YoutubeSettingsDto? Settings, string? Error) Save(YoutubeSettingsDto s)
    {
        if (s.SyncIntervalHours is < 0 or > 24 * 7) return (null, Tx.Key("errors.yt.syncInterval"));
        if (s.AudioFormat is not ("original" or "mp3")) return (null, Tx.Key("errors.yt.audioFormat"));
        if (s.AutoAcceptThreshold is < 0 or > 1) return (null, Tx.Key("errors.yt.threshold"));
        if (s.ParallelDownloads is < 1 or > MaxParallelDownloads) return (null, Tx.Key("errors.yt.parallelDownloads", ("max", MaxParallelDownloads)));
        var clean = s with { AcoustIdKey = string.IsNullOrWhiteSpace(s.AcoustIdKey) ? null : s.AcoustIdKey.Trim() };
        lock (_lock)
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(clean, Json));
            _settings = clean;
        }
        Changed?.Invoke();
        return (clean, null);
    }

    public string? CookiesPath => File.Exists(_cookiesPath) ? _cookiesPath : null;

    public DateTimeOffset? CookiesUpdatedAt => File.Exists(_cookiesPath) ? File.GetLastWriteTimeUtc(_cookiesPath) : null;

    /// <summary>Enregistre un export cookies.txt. Seules les lignes YouTube/Google sont gardées.</summary>
    public string? SaveCookies(string content)
    {
        var lines = content.Replace("\r", "").Split('\n');
        var kept = lines.Where(l =>
        {
            var fields = l.Split('\t');
            if (fields.Length != 7) return false;
            var domain = fields[0].Replace("#HttpOnly_", "").TrimStart('.');
            return domain is "youtube.com" or "google.com" || domain.EndsWith(".youtube.com", StringComparison.Ordinal) || domain.EndsWith(".google.com", StringComparison.Ordinal);
        }).ToList();
        if (kept.Count == 0) return Tx.Key("errors.yt.noCookies");
        File.WriteAllText(_cookiesPath, "# Netscape HTTP Cookie File\n" + string.Join('\n', kept) + "\n");
        return null;
    }

    public void DeleteCookies()
    {
        if (File.Exists(_cookiesPath)) File.Delete(_cookiesPath);
    }
}
