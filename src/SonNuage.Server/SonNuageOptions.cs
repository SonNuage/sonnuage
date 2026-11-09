namespace SonNuage.Server;

public sealed class SonNuageOptions
{
    public const string Section = "SonNuage";

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

    /// <summary>yt-dlp et son moteur JavaScript (YouTube l'exige). Vides : dossier des outils puis PATH.</summary>
    public string? YtDlpPath { get; set; }
    public string? DenoPath { get; set; }
    /// <summary>fpcalc (Chromaprint) pour l'empreinte acoustique AcoustID. Facultatif.</summary>
    public string FpcalcPath { get; set; } = "fpcalc";
    /// <summary>Contact transmis à MusicBrainz dans le User-Agent, comme l'exigent ses règles d'usage.</summary>
    public string MusicBrainzContact { get; set; } = "https://github.com/SonNuage/sonnuage";
    /// <summary>Bases publiques de la découverte (remplacées dans les tests pour ne rien appeler).</summary>
    public string MusicBrainzUrl { get; set; } = "https://musicbrainz.org";
    public string ListenBrainzUrl { get; set; } = "https://api.listenbrainz.org";
    public string ListenBrainzLabsUrl { get; set; } = "https://labs.api.listenbrainz.org";
    public string DeezerUrl { get; set; } = "https://api.deezer.com";
    /// <summary>Base de LRCLIB, d'où viennent les paroles (remplacée dans les tests pour ne rien appeler).</summary>
    public string LrclibUrl { get; set; } = "https://lrclib.net";
    /// <summary>
    /// Recherche par correspondance (demandes, découverte) : une vidéo de la chaîne officielle de l'artiste qui atteint
    /// cette note (sur 100) passe devant toutes les autres. 101 pour ne plus favoriser la chaîne officielle.
    /// </summary>
    public int MatchOfficialMinScore { get; set; } = Youtube.YoutubeMatcher.DefaultOfficialMinScore;
    /// <summary>Jeton utilisateur ListenBrainz (facultatif) : certains points d'accès l'exigent, sinon Deezer prend le relais.</summary>
    public string? ListenBrainzToken { get; set; }

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

    /// <summary>Gestionnaire de tâches (imports, rescans).</summary>
    public JobOptions Jobs { get; set; } = new();

    public string DatabasePath => Path.Combine(DataPath, "sonnuage.db");
    public string TranscodeCachePath => Path.Combine(DataPath, "cache", "transcode");
    public string CoverCachePath => Path.Combine(DataPath, "cache", "covers");
    public string PreviewCachePath => Path.Combine(DataPath, "cache", "previews");
    public string DiscoveryPreviewPath => Path.Combine(DataPath, "discovery", "previews");
    public string UploadTempPath => Path.Combine(DataPath, "uploads-tmp");
    /// <summary>Où atterrissent les morceaux importés de YouTube (sous le dossier d'envoi : supprimables depuis l'admin).</summary>
    public string YoutubePath => Path.Combine(UploadPath, "YouTube");
    public string YoutubeDataPath => Path.Combine(DataPath, "youtube");
    public string LyricsCachePath => Path.Combine(DataPath, "lyrics");
    /// <summary>yt-dlp et Deno téléchargés par la mise à jour depuis l'admin (prioritaires sur ceux de l'image).</summary>
    public string ToolsPath => Path.Combine(DataPath, "tools");
}

public sealed class JobOptions
{
    /// <summary>
    /// Exécute les tâches dans ce processus. Plusieurs instances partageant la même base peuvent consommer la file
    /// ensemble : les plafonds par file (une seule recherche AcoustID à la fois…) restent globaux.
    /// </summary>
    public bool Worker { get; set; } = true;

    /// <summary>Nom stable de l'instance : à son redémarrage, ses tâches interrompues repartent aussitôt.</summary>
    public string WorkerName { get; set; } = "main";

    /// <summary>Durée de conservation des suites terminées.</summary>
    public int RetentionDays { get; set; } = 14;
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
    public string Index { get; set; } = "sonnuage";

    /// <summary>Au-delà, la requête bascule sur la recherche en base pendant qu'Elasticsearch termine en arrière-plan.</summary>
    public int TimeoutMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Identifiant d'un endpoint d'inférence Elasticsearch (ex. un modèle E5 multilingue) pour ajouter une
    /// recherche sémantique vectorielle (champ semantic_text). Vide : recherche textuelle floue uniquement.
    /// </summary>
    public string? SemanticInferenceId { get; set; }
}
