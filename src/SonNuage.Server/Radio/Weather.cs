using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;

namespace SonNuage.Server.Radio;

/// <summary>Lieu trouvé par le géocodage : nom affiché (« Lyon, France ») et coordonnées.</summary>
public sealed record WeatherPlace(string Name, double Latitude, double Longitude);

/// <summary>Météo du lieu réglé dans l'admin, et recherche d'un lieu par son nom.</summary>
public interface IWeatherSource
{
    /// <summary>Temps qu'il fait au lieu réglé ; null sans lieu, radio météo coupée ou service injoignable.</summary>
    Task<WeatherNow?> NowAsync(CancellationToken ct);
    Task<WeatherPlace?> FindPlaceAsync(string name, string language, CancellationToken ct);
}

/// <summary>Réglages des radios dans <c>radio.json</c> du dossier de données.</summary>
public sealed class RadioSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dir;
    private readonly string _path;
    private readonly Lock _lock = new();
    private RadioSettingsDto _settings = new(null, null, null);

    public RadioSettingsStore(IOptions<SonNuageOptions> options)
    {
        _dir = options.Value.DataPath;
        _path = Path.Combine(_dir, "radio.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<RadioSettingsDto>(File.ReadAllText(_path), Json) is { } saved) _settings = saved;
        }
        catch (JsonException)
        {
        }
    }

    public RadioSettingsDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    public void Save(RadioSettingsDto settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
            _settings = settings;
        }
    }
}

/// <summary>
/// Open-Meteo (gratuit, sans compte) : géocodage du lieu et temps actuel. Seules les coordonnées du lieu réglé
/// partent vers Open-Meteo. Réponse gardée 30 minutes (10 minutes après un échec).
/// </summary>
public sealed class OpenMeteoWeather(IHttpClientFactory httpFactory, RadioSettingsStore settings, IMemoryCache cache, ILogger<OpenMeteoWeather> logger)
    : IWeatherSource
{
    public const string HttpClientName = "open-meteo";
    internal static string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    internal static string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(10);

    public async Task<WeatherNow?> NowAsync(CancellationToken ct)
    {
        var s = settings.Settings;
        if (!s.WeatherEnabled || s.Place is null || s.Latitude is not { } lat || s.Longitude is not { } lon) return null;
        var key = string.Create(CultureInfo.InvariantCulture, $"weather:{lat:F3}:{lon:F3}");
        if (cache.TryGetValue(key, out WeatherNow? known)) return known;
        WeatherNow? now = null;
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{ForecastUrl}?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,weather_code,is_day&timezone=auto");
            var json = JsonNode.Parse(await httpFactory.CreateClient(HttpClientName).GetStringAsync(url, ct));
            if (json?["current"] is JsonObject c && c["weather_code"] is { } code && c["temperature_2m"] is { } temp)
                now = new WeatherNow(code.GetValue<int>(), temp.GetValue<double>(), c["is_day"]?.GetValue<int>() != 0, ShortName(s.Place));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Météo indisponible : {Message}", ex.Message);
        }
        cache.Set(key, now, now is null ? FailedFor : KeepFor);
        return now;
    }

    public async Task<WeatherPlace?> FindPlaceAsync(string name, string language, CancellationToken ct)
    {
        var url = $"{GeocodingUrl}?count=1&format=json&language={Uri.EscapeDataString(language)}&name={Uri.EscapeDataString(name.Trim())}";
        var json = JsonNode.Parse(await httpFactory.CreateClient(HttpClientName).GetStringAsync(url, ct));
        if (json?["results"]?.AsArray().FirstOrDefault() is not JsonObject r) return null;
        var parts = new[] { r["name"]?.GetValue<string>(), r["country"]?.GetValue<string>() }.Where(p => !string.IsNullOrWhiteSpace(p));
        return new WeatherPlace(string.Join(", ", parts), r["latitude"]!.GetValue<double>(), r["longitude"]!.GetValue<double>());
    }

    /// <summary>« Lyon, France » → « Lyon » pour la description de la radio.</summary>
    private static string ShortName(string place) => place.Split(',')[0].Trim();
}
