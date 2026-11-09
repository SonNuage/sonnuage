using System.Text.Json;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Réglage de l'analyse audio, modifiable dans l'admin (fichier <c>analysis.json</c> du dossier de données).
/// Activée par défaut : elle ne tourne de toute façon que si l'analyseur est installé.
/// </summary>
public sealed class AudioAnalysisSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dir;
    private readonly string _path;
    private readonly Lock _lock = new();
    private AudioAnalysisSettingsDto _settings = new(Enabled: true);

    public AudioAnalysisSettingsStore(IOptions<SonNuageOptions> options)
    {
        _dir = options.Value.DataPath;
        _path = Path.Combine(_dir, "analysis.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<AudioAnalysisSettingsDto>(File.ReadAllText(_path), Json) is { } saved)
                _settings = saved;
        }
        catch (JsonException)
        {
        }
    }

    public AudioAnalysisSettingsDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    public void Save(AudioAnalysisSettingsDto s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(s, Json));
            _settings = s;
        }
    }
}
