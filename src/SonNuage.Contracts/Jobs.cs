namespace SonNuage.Contracts;

/// <summary>État d'une file d'exécution : capacité (tâches simultanées), en cours, en attente.</summary>
public sealed record JobLaneDto(string Lane, int Capacity, int Running, int Queued);

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
