namespace Localify.Server;

public sealed class LocalifyOptions
{
    public const string Section = "Localify";

    /// <summary>Dossiers de musique scannés (montés en lecture seule de préférence).</summary>
    public List<string> MusicPaths { get; set; } = [];

    /// <summary>Dossier inscriptible où atterrissent les fichiers envoyés depuis l'admin. Scanné aussi.</summary>
    public string UploadPath { get; set; } = "uploads";

    /// <summary>Base SQLite, clé de signature, caches.</summary>
    public string DataPath { get; set; } = "var";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Taille max du cache de transcodage avant purge LRU.</summary>
    public long TranscodeCacheMaxMegabytes { get; set; } = 10_240;

    public int MaxConcurrentTranscodes { get; set; } = 4;

    /// <summary>Intervalle entre deux scans complets automatiques. 0 = désactivé.</summary>
    public int ScanIntervalMinutes { get; set; } = 360;

    public bool WatchFileSystem { get; set; } = true;

    public string? JwtSigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 60;

    public int MediaTokenHours { get; set; } = 24;

    /// <summary>Tentatives de connexion/refresh par minute et par IP (limite le brute-force).</summary>
    public int LoginAttemptsPerMinute { get; set; } = 10;

    /// <summary>Compte admin créé au premier démarrage si la base ne contient aucun utilisateur.</summary>
    public string? InitialAdminUser { get; set; } = "admin";

    public string? InitialAdminPassword { get; set; }

    /// <summary>
    /// Fait confiance à CF-Connecting-IP / X-Forwarded-*. À n'activer que si le serveur n'est joignable
    /// qu'au travers de Cloudflare (tunnel ou firewall limité aux IP Cloudflare).
    /// </summary>
    public bool BehindProxy { get; set; } = true;

    public string DatabasePath => Path.Combine(DataPath, "localify.db");
    public string TranscodeCachePath => Path.Combine(DataPath, "cache", "transcode");
    public string CoverCachePath => Path.Combine(DataPath, "cache", "covers");
    public string UploadTempPath => Path.Combine(DataPath, "uploads-tmp");
}
