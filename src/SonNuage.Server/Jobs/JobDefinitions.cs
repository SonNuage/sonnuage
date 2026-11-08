using System.Text.Json;
using System.Text.Json.Nodes;
using SonNuage.Server.Data;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Files d'exécution. Chacune a sa capacité (tâches simultanées, tous consommateurs confondus) : c'est elle qui
/// garantit par exemple qu'une seule recherche AcoustID tourne à la fois, qu'elle vienne d'un import ou d'un rescan.
/// </summary>
public static class JobLanes
{
    /// <summary>Téléchargements YouTube (capacité réglée dans l'admin YouTube).</summary>
    public const string Download = "download";
    /// <summary>Empreintes et recherches AcoustID : une à la fois, quoi qu'il arrive.</summary>
    public const string AcoustId = "acoustid";
    /// <summary>MusicBrainz, tags, rangement : une à la fois (MusicBrainz limite à une requête par seconde).</summary>
    public const string Metadata = "metadata";
    /// <summary>Gros traitements sur la bibliothèque, un à la fois : scan complet, réindexation, purge du cache, préparation d'un rescan.</summary>
    public const string Library = "library";
    /// <summary>Synchronisation des abonnements YouTube (lecture des playlists et chaînes) : une à la fois.</summary>
    public const string Sync = "sync";

    public static readonly IReadOnlyList<string> All = [Download, AcoustId, Metadata, Library, Sync];
}

/// <summary>Type de tâche déclaré : nom, file, priorité par défaut, nombre de tentatives.</summary>
public sealed record JobKind(string Name, string Lane, int Priority = 0, int MaxAttempts = 1);

public static class JobKinds
{
    public static readonly JobKind YoutubeDownload = new("youtube.download", JobLanes.Download, Priority: 10);
    public static readonly JobKind AcoustIdIdentify = new("acoustid.identify", JobLanes.AcoustId, MaxAttempts: 3);
    public static readonly JobKind YoutubeMetadata = new("youtube.metadata", JobLanes.Metadata, Priority: 10);
    public static readonly JobKind LibraryAcoustIdScan = new("library.acoustid-scan", JobLanes.Library);
    public static readonly JobKind LibraryMatch = new("library.match", JobLanes.Metadata, MaxAttempts: 2);
    /// <summary>Scan complet : passe devant la préparation d'un rescan, qui doit voir la bibliothèque à jour.</summary>
    public static readonly JobKind LibraryScan = new("library.scan", JobLanes.Library, Priority: 5);
    public static readonly JobKind SearchReindex = new("search.reindex", JobLanes.Library, MaxAttempts: 3);
    public static readonly JobKind TranscodePurge = new("transcode.purge", JobLanes.Library);
    public static readonly JobKind SubscriptionSync = new("youtube.sync", JobLanes.Sync);
}

/// <summary>Types de suites (création depuis l'admin, filtres).</summary>
public static class JobSuiteKinds
{
    public const string YoutubeImport = "youtube.import";
    public const string AcoustIdRescan = "library.acoustid-rescan";
    public const string LibraryScan = "library.scan";
    public const string SearchReindex = "search.reindex";
    public const string TranscodePurge = "transcode.purge";
    public const string SubscriptionSync = "youtube.sync";
}

public interface IJobHandler
{
    JobKind Kind { get; }

    Task RunAsync(JobContext ctx);

    /// <summary>La tâche a été annulée depuis l'admin (en attente ou en cours) : remettre son objet dans un état cohérent.</summary>
    Task CanceledAsync(IServiceProvider services, Guid? subjectId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Plus rien à faire (objet supprimé, déjà traité ailleurs) : la tâche et la suite s'arrêtent sans erreur.</summary>
public sealed class JobAbortedException(string reason) : Exception(reason);

/// <summary>La tâche ne peut pas avancer pour l'instant (YouTube en pause…) : elle retourne dans la file jusqu'à <see cref="Until"/>.</summary>
public sealed class JobDeferredException(DateTimeOffset until, string reason) : Exception(reason)
{
    public DateTimeOffset Until { get; } = until;
}

/// <summary>Ce que voit une tâche pendant son exécution.</summary>
public sealed class JobContext(Job job, JsonObject? input, IServiceProvider services, JobEngine engine, CancellationToken ct)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid JobId => job.Id;
    public Guid SuiteId => job.SuiteId;
    public Guid? SubjectId => job.SubjectId;
    public string Title => job.Title;
    public int Attempt => job.Attempts;
    public int Priority => job.Priority;
    public JsonObject Payload { get; } = (job.Payload is null ? null : JsonNode.Parse(job.Payload) as JsonObject) ?? [];
    /// <summary>Sortie de la tâche précédente de la suite (celle dont celle-ci dépend).</summary>
    public JsonObject Input { get; } = input ?? [];
    /// <summary>Sortie transmise à la tâche suivante.</summary>
    public JsonObject Output { get; } = [];
    /// <summary>Services de la portée de la tâche (base de données…).</summary>
    public IServiceProvider Services => services;
    public CancellationToken CancellationToken => ct;
    internal double? Progress { get; private set; }

    public T? Get<T>(string name) => Payload[name] is { } n ? n.Deserialize<T>(Json) : default;

    public void Info(string message) => engine.Log(job.Id, "info", message);

    public void Warn(string message) => engine.Log(job.Id, "warn", message);

    public void Report(double progress) => Progress = Math.Clamp(progress, 0, 1);

    public void Set<T>(string name, T value) => Output[name] = JsonSerializer.SerializeToNode(value, Json);

    /// <summary>Ajoute à la suite de cette tâche les tâches d'un plan (tâche qui en prépare d'autres).</summary>
    public Task AppendAsync(SonNuageDbContext db, JobPlan plan) => engine.AppendAsync(db, job.SuiteId, plan, ct);
}

/// <summary>
/// Plan d'une suite, à confier à <see cref="JobEngine.EnqueueAsync"/> :
/// <code>
/// var plan = new JobPlan(JobSuiteKinds.YoutubeImport, title);
/// plan.Then(JobKinds.YoutubeDownload, "Téléchargement", payload).Then(JobKinds.AcoustIdIdentify, "Empreinte")…
/// </code>
/// </summary>
public sealed class JobPlan(string kind, string title, Guid? createdById = null)
{
    public JobSuite Suite { get; } = new() { Kind = kind, Title = Truncate(title, 300), CreatedById = createdById };
    public List<Job> Jobs { get; } = [];
    private Job? _last;

    /// <summary>Ajoute une tâche. <paramref name="after"/> = celle qui doit réussir avant (sa sortie devient l'entrée).</summary>
    public JobPlan Add(JobKind kind, string title, object? payload = null, Job? after = null, Guid? subjectId = null, int? priority = null)
    {
        var job = new Job
        {
            SuiteId = Suite.Id,
            Kind = kind.Name,
            Lane = kind.Lane,
            Title = Truncate(title, 300),
            Payload = payload is null ? null : JsonSerializer.Serialize(payload, JobContext.Json),
            DependsOnId = after?.Id,
            SubjectId = subjectId,
            Priority = priority ?? kind.Priority,
            MaxAttempts = kind.MaxAttempts,
            Status = after is null ? JobStatus.Queued : JobStatus.Pending,
        };
        Jobs.Add(job);
        _last = job;
        return this;
    }

    /// <summary>Ajoute une tâche qui suit la dernière ajoutée.</summary>
    public JobPlan Then(JobKind kind, string title, object? payload = null, Guid? subjectId = null, int? priority = null) =>
        Add(kind, title, payload, _last, subjectId, priority);

    public Job Last => _last ?? throw new InvalidOperationException("Plan vide.");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
