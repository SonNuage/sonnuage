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

    /// <summary>Moteur de recherche externe facultatif. Sans URL, la recherche se fait en base.</summary>
    public SearchOptions Search { get; set; } = new();

    public string DatabasePath => Path.Combine(DataPath, "localify.db");
    public string TranscodeCachePath => Path.Combine(DataPath, "cache", "transcode");
    public string CoverCachePath => Path.Combine(DataPath, "cache", "covers");
    public string UploadTempPath => Path.Combine(DataPath, "uploads-tmp");
}

public sealed class SearchOptions
{
    /// <summary>URL d'Elasticsearch (ex. http://elasticsearch:9200). Vide : recherche en base uniquement.</summary>
    public string? Url { get; set; }

    /// <summary>Clé d'API (en-tête « ApiKey … »), prioritaire sur l'utilisateur / mot de passe.</summary>
    public string? ApiKey { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// Empreinte SHA-256 du certificat (CA) d'Elasticsearch, affichée au premier démarrage d'un nœud 8+/9+
    /// en HTTPS auto-signé. Permet de faire confiance à ce certificat sans désactiver la vérification TLS.
    /// </summary>
    public string? CertificateFingerprint { get; set; }

    /// <summary>Préfixe des index créés.</summary>
    public string Index { get; set; } = "localify";

    /// <summary>Au-delà, la requête bascule sur la recherche en base pendant qu'Elasticsearch termine en arrière-plan.</summary>
    public int TimeoutMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Identifiant d'un endpoint d'inférence Elasticsearch (ex. un modèle E5 multilingue) pour ajouter une
    /// recherche sémantique vectorielle (champ semantic_text). Vide : recherche textuelle floue uniquement.
    /// </summary>
    public string? SemanticInferenceId { get; set; }
}
