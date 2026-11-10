namespace SonNuage.Contracts;

/// <summary>
/// État d'une file d'exécution : capacité (tâches simultanées), en cours, en attente, et pause demandée depuis l'admin
/// (ses tâches attendent la reprise ; celle qui tournait a été interrompue et reprendra). QuietUntil : file retenue par
/// les heures calmes jusqu'à cette heure.
/// </summary>
public sealed record JobLaneDto(string Lane, int Capacity, int Running, int Queued, bool Paused = false, DateTimeOffset? QuietUntil = null);

/// <summary>
/// Heures calmes : de Start à End (heure locale du fuseau TimeZone, identifiant IANA ; la plage peut passer minuit),
/// les files listées ne démarrent rien et leurs tâches en cours sont interrompues, puis reprennent à la fin de la plage.
/// </summary>
public sealed record QuietHoursDto(bool Enabled, TimeOnly Start, TimeOnly End, IReadOnlyList<string> Lanes, string TimeZone)
{
    public static readonly QuietHoursDto Defaults = new(false, new TimeOnly(8, 0), new TimeOnly(23, 0), ["analysis"], "Europe/Paris");
}

/// <summary>
/// Suite de tâches. Status : « Running », « Queued » (rien ne tourne encore), « Failed », « Canceled » ou « Succeeded ».
/// Titre et titres des tâches peuvent contenir des marqueurs de traduction (voir Tx).
/// </summary>
public sealed record JobSuiteDto(
    Guid Id,
    string Kind,
    string Title,
    string Status,
    int Total,
    int Waiting,
    int Running,
    int Succeeded,
    int Failed,
    int Canceled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

public sealed record JobsOverviewDto(IReadOnlyList<JobLaneDto> Lanes, PagedResult<JobSuiteDto> Suites);

public sealed record JobDto(
    Guid Id,
    Guid SuiteId,
    Guid? DependsOnId,
    string Kind,
    string Lane,
    // Pending (attend la tâche précédente), Queued, Running, Succeeded, Failed, Canceled.
    string Status,
    string Title,
    string? Error,
    int Attempts,
    int MaxAttempts,
    double? Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    // Pour une tâche en attente : pas avant (pause YouTube, nouvelle tentative).
    DateTimeOffset NotBefore);

/// <summary>Paramètres et sortie (JSON) en plus.</summary>
public sealed record JobDetailDto(JobDto Job, string? Payload, string? Output);

public sealed record JobLogDto(long Id, DateTimeOffset At, string Level, string Message);

/// <summary>Tâches créables depuis l'admin. Kind : « acoustid-rescan » (ArtistId absent = toute la bibliothèque).</summary>
/// <param name="Threshold">Seuil de correction automatique (0,5 à 1). Null = celui des réglages YouTube.</param>
/// <param name="IncludeIdentified">Rescan : vérifier aussi les morceaux déjà identifiés (identifiant MusicBrainz connu).</param>
public sealed record CreateJobRequest(string Kind, Guid? ArtistId, bool Apply, double? Threshold = null, bool IncludeIdentified = false);

/// <summary>
/// Événements du flux en direct du volet Tâches (<c>GET /api/admin/jobs/events</c>, Server-Sent Events). Chacun porte
/// l'état complet de ce qu'il nomme, envoyé à l'ouverture puis à chaque changement.
/// </summary>
public static class JobStreamEvents
{
    /// <summary><see cref="JobsOverviewDto"/> : files et page de suites (paramètres status, kind, q, limit).</summary>
    public const string Overview = "overview";
    /// <summary>PagedResult&lt;JobDto&gt; : tâches de la suite ouverte (paramètres suite, suiteLimit).</summary>
    public const string Suite = "suite";
    /// <summary><see cref="JobDetailDto"/> : tâche ouverte (paramètre job).</summary>
    public const string Job = "job";
    /// <summary>List&lt;JobLogDto&gt; : nouvelles lignes du journal de la tâche ouverte (après le paramètre after).</summary>
    public const string Logs = "logs";
}

/// <summary>Types de tâches planifiables (voir <see cref="JobScheduleRequest"/>).</summary>
public static class ScheduledJobKinds
{
    /// <summary>Rescan AcoustID de toute la bibliothèque (options : <see cref="JobScheduleOptions"/>).</summary>
    public const string AcoustIdRescan = "acoustid-rescan";
    public const string LibraryScan = "library-scan";
    public const string SearchReindex = "search-reindex";
    public const string TranscodePurge = "transcode-purge";
    /// <summary>Analyse audio des morceaux en attente (si l'analyse est activée).</summary>
    public const string AudioAnalysis = "audio-analysis";
    public const string DiscoveryRefresh = "discovery-refresh";
    public const string DatabaseBackup = "db-backup";

    public static readonly IReadOnlyList<string> All =
        [AcoustIdRescan, LibraryScan, SearchReindex, TranscodePurge, AudioAnalysis, DiscoveryRefresh, DatabaseBackup];
}

/// <summary>Options du rescan AcoustID planifié, comme à la création d'une tâche depuis l'admin.</summary>
/// <param name="Threshold">Seuil de correction automatique (0,5 à 1). Null = celui des réglages YouTube.</param>
public sealed record JobScheduleOptions(bool Apply = false, double? Threshold = null, bool IncludeIdentified = false);

/// <summary>
/// Création ou modification d'une tâche planifiée : chaque jour de <paramref name="Days"/> (0 = dimanche … 6 = samedi,
/// comme <see cref="DayOfWeek"/>) à l'heure <paramref name="At"/> du fuseau <paramref name="TimeZone"/> (identifiant IANA).
/// </summary>
public sealed record JobScheduleRequest(string Kind, IReadOnlyList<int> Days, TimeOnly At, string TimeZone, bool Enabled = true,
    JobScheduleOptions? Options = null);

/// <summary>Tâche planifiée. NextRunAt : prochain passage (null si elle est désactivée).</summary>
public sealed record JobScheduleDto(
    Guid Id,
    string Kind,
    IReadOnlyList<int> Days,
    TimeOnly At,
    string TimeZone,
    bool Enabled,
    JobScheduleOptions? Options,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt,
    Guid? LastSuiteId,
    DateTimeOffset? NextRunAt);
