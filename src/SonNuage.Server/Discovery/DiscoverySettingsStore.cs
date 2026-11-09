using System.Text.Json;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Réglages de la découverte, modifiables dans l'admin (fichier <c>discovery.json</c> du dossier de données) :
/// fréquence d'actualisation, nouveaux artistes par actualisation, minimum affiché, durée de vie d'une proposition,
/// titres phares par artiste.
/// </summary>
public sealed class DiscoverySettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dir;
    private readonly string _path;
    private readonly Lock _lock = new();
    private DiscoverySettingsDto _settings = DiscoverySettingsDto.Defaults;

    public DiscoverySettingsStore(IOptions<SonNuageOptions> options)
    {
        _dir = options.Value.DataPath;
        _path = Path.Combine(_dir, "discovery.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<DiscoverySettingsDto>(File.ReadAllText(_path), Json) is { } saved && Error(saved) is null)
                _settings = saved;
        }
        catch (JsonException)
        {
        }
    }

    public DiscoverySettingsDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    /// <summary>Enregistre les réglages ; renvoie le message d'erreur s'ils sortent des limites.</summary>
    public string? Save(DiscoverySettingsDto s)
    {
        if (Error(s) is { } error) return error;
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(s, Json));
            _settings = s;
        }
        return null;
    }

    private static string? Error(DiscoverySettingsDto s) =>
        s.RefreshHours is < 1 or > DiscoverySettingsDto.MaxRefreshHours
        || s.NewPerRun is < 0 or > DiscoverySettingsDto.MaxNewPerRun
        || s.MinShown is < 0 or > DiscoverySettingsDto.MaxMinShown
        || s.MaxAgeDays is < 1 or > DiscoverySettingsDto.MaxMaxAgeDays
        || s.TracksPerArtist is < 1 or > DiscoverySettingsDto.MaxTracksPerArtist
            ? Tx.Key("errors.discovery.settings", ("hours", DiscoverySettingsDto.MaxRefreshHours), ("perRun", DiscoverySettingsDto.MaxNewPerRun),
                ("shown", DiscoverySettingsDto.MaxMinShown), ("days", DiscoverySettingsDto.MaxMaxAgeDays), ("tracks", DiscoverySettingsDto.MaxTracksPerArtist))
            : null;
}
