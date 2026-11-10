using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Storage;

/// <summary>
/// Sauvegardes de la base SQLite : copie à chaud cohérente (<c>VACUUM INTO</c>, sans arrêter le serveur) dans le
/// dossier des sauvegardes, puis ménage des plus anciennes. Restaurer : arrêter le serveur, remplacer
/// <c>sonnuage.db</c> par la copie et supprimer <c>sonnuage.db-wal</c> / <c>sonnuage.db-shm</c>.
/// </summary>
public sealed partial class DatabaseBackups(IOptions<SonNuageOptions> options, TimeProvider time)
{
    private const string Format = "yyyyMMdd-HHmmss";

    [GeneratedRegex(@"^sonnuage-(\d{8}-\d{6})\.db$")]
    private static partial Regex Name();

    public string Folder => options.Value.BackupFolder;

    /// <summary>Sauvegardes présentes, la plus récente en tête.</summary>
    public List<BackupFileDto> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return new DirectoryInfo(Folder).EnumerateFiles("sonnuage-*.db")
            .Select(f => (File: f, Match: Name().Match(f.Name)))
            .Where(x => x.Match.Success)
            .Select(x => new BackupFileDto(x.File.Name, x.File.Length,
                DateTime.TryParseExact(x.Match.Groups[1].Value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                    ? new DateTimeOffset(at, TimeSpan.Zero) : new DateTimeOffset(x.File.LastWriteTimeUtc, TimeSpan.Zero)))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Copie la base (nom horodaté en UTC). Écrite sous un nom temporaire : une copie interrompue n'est jamais listée.</summary>
    public async Task<BackupFileDto> CreateAsync(SonNuageDbContext db, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var name = $"sonnuage-{time.GetUtcNow().ToString(Format, CultureInfo.InvariantCulture)}.db";
        var path = Path.Combine(Folder, name);
        var temp = Path.Combine(Folder, $".{name}.tmp");
        File.Delete(temp);
        try
        {
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {temp}", ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        return new BackupFileDto(name, new FileInfo(path).Length, time.GetUtcNow());
    }

    /// <summary>Supprime les sauvegardes au-delà des <paramref name="keep"/> plus récentes (les autres fichiers restent).</summary>
    public static int Prune(string folder, int keep)
    {
        if (!Directory.Exists(folder)) return 0;
        var old = Directory.EnumerateFiles(folder, "sonnuage-*.db")
            .Where(f => Name().IsMatch(Path.GetFileName(f)))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(Math.Max(1, keep))
            .ToList();
        foreach (var f in old) File.Delete(f);
        return old.Count;
    }

    /// <summary>Dernier horaire de sauvegarde passé (ou atteint) à cet instant.</summary>
    public static DateTimeOffset LastSlot(BackupSettingsDto s, DateTimeOffset now)
    {
        var zone = QuietHours.Zone(s.TimeZone) ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var slot = local.Date + s.At.ToTimeSpan();
        if (slot > local.DateTime) slot = slot.AddDays(-1);
        return new DateTimeOffset(slot, zone.GetUtcOffset(slot)).ToUniversalTime();
    }
}

/// <summary>Réglages des sauvegardes, modifiables dans l'admin (fichier <c>backup.json</c> du dossier de données).</summary>
public sealed class BackupSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dir;
    private readonly string _path;
    private readonly Lock _lock = new();
    private BackupSettingsDto _settings = BackupSettingsDto.Defaults;

    public BackupSettingsStore(IOptions<SonNuageOptions> options)
    {
        _dir = options.Value.DataPath;
        _path = Path.Combine(_dir, "backup.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<BackupSettingsDto>(File.ReadAllText(_path), Json) is { } saved)
                _settings = saved;
        }
        catch (JsonException)
        {
        }
    }

    public BackupSettingsDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    public void Save(BackupSettingsDto s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(s, Json));
            _settings = s;
        }
    }
}

/// <summary>Une sauvegarde, puis le ménage des plus anciennes.</summary>
public sealed class DatabaseBackupJob(DatabaseBackups backups, BackupSettingsStore settings) : IJobHandler
{
    public JobKind Kind => JobKinds.DatabaseBackup;

    public async Task RunAsync(JobContext ctx)
    {
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var file = await backups.CreateAsync(db, ctx.CancellationToken);
        var removed = DatabaseBackups.Prune(backups.Folder, settings.Settings.Keep);
        ctx.Info(Tx.Key("jobs.maintenance.backedUp", ("name", file.Name), ("mb", Math.Round(file.Bytes / 1048576.0, 1)), ("removed", removed)));
    }
}

/// <summary>
/// Demande la sauvegarde du jour une fois son horaire passé (vérifié toutes les 10 minutes) : un serveur éteint à
/// l'heure prévue sauvegarde à son retour.
/// </summary>
public sealed class BackupScheduler(IServiceScopeFactory scopes, BackupSettingsStore settings, DatabaseBackups backups, TimeProvider time,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Sauvegarde de la base non planifiée");
                }
                await Task.Delay(CheckEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var s = settings.Settings;
        if (!s.Enabled) return;
        var slot = DatabaseBackups.LastSlot(s, time.GetUtcNow());
        if (backups.List().FirstOrDefault() is { } last && last.CreatedAt >= slot) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        // Déjà tentée depuis l'horaire (même en échec) : pas de nouvel essai avant le prochain.
        if (await db.Jobs.AnyAsync(j => j.Kind == JobKinds.DatabaseBackup.Name && j.CreatedAt >= slot, ct)) return;
        await scopes.RequestAsync(MaintenanceJobs.DatabaseBackup(), ct);
    }
}
