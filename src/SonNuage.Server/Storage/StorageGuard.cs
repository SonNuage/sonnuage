using System.Text.Json;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Api;

namespace SonNuage.Server.Storage;

/// <summary>Plus assez de place sur le disque pour télécharger (voir <see cref="StorageGuard"/>).</summary>
public sealed class StorageFullException(string message) : Exception(message);

/// <summary>
/// Garde-fous disque réglables dans l'admin (fichier <c>storage.json</c> du dossier de données) : espace libre
/// minimum sous lequel tout téléchargement attend, et place allouée aux extraits.
/// </summary>
public sealed class StorageGuard
{
    private const double Gb = 1024d * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SonNuageOptions _options;
    private readonly string _path;
    private readonly Lock _lock = new();
    private StorageSettingsDto _settings = StorageSettingsDto.Defaults;

    public StorageGuard(IOptions<SonNuageOptions> options)
    {
        _options = options.Value;
        _path = Path.Combine(_options.DataPath, "storage.json");
        try
        {
            if (File.Exists(_path)) _settings = JsonSerializer.Deserialize<StorageSettingsDto>(File.ReadAllText(_path), Json) ?? _settings;
        }
        catch (JsonException)
        {
        }
    }

    public StorageSettingsDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    public string? Save(StorageSettingsDto s)
    {
        if (!double.IsFinite(s.MinFreeGb) || s.MinFreeGb is < 0 or > StorageSettingsDto.MaxMinFreeGb)
            return Tx.Key("errors.storage.minFree", ("max", StorageSettingsDto.MaxMinFreeGb));
        if (!double.IsFinite(s.PreviewQuotaGb) || s.PreviewQuotaGb is < StorageSettingsDto.MinPreviewQuotaGb or > StorageSettingsDto.MaxPreviewQuotaGb)
            return Tx.Key("errors.storage.previewQuota", ("min", StorageSettingsDto.MinPreviewQuotaGb), ("max", StorageSettingsDto.MaxPreviewQuotaGb));
        lock (_lock)
        {
            Directory.CreateDirectory(_options.DataPath);
            File.WriteAllText(_path, JsonSerializer.Serialize(s, Json));
            _settings = s;
        }
        return null;
    }

    public long PreviewQuotaBytes => (long)(Settings.PreviewQuotaGb * Gb);

    /// <summary>Disque de la musique téléchargée (dossier des envois et de YouTube).</summary>
    public DashboardEndpoints.Disk? MusicDisk => DiskOf(_options.UploadPath);

    /// <summary>Vrai quand l'espace libre est passé sous le minimum (disque illisible : on ne bloque pas).</summary>
    public bool DownloadsBlocked => MusicDisk is { } disk && disk.FreeBytes < Settings.MinFreeGb * Gb;

    /// <summary>Lève <see cref="StorageFullException"/> si l'espace libre est sous le minimum.</summary>
    public void EnsureRoom()
    {
        if (DownloadsBlocked) throw new StorageFullException(LowSpaceMessage());
    }

    public string LowSpaceMessage() =>
        Tx.Key("errors.storage.lowSpace", ("free", Math.Round((MusicDisk?.FreeBytes ?? 0) / Gb, 1)), ("min", Settings.MinFreeGb));

    public StorageStatusDto Status(long previewBytes)
    {
        var disk = MusicDisk;
        return new StorageStatusDto(Settings, disk?.FreeBytes, disk?.TotalBytes, previewBytes, DownloadsBlocked);
    }

    private static DashboardEndpoints.Disk? DiskOf(string path)
    {
        try
        {
            return DashboardEndpoints.DiskOf(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
