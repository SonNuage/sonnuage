using System.Text.Json;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;

namespace SonNuage.Server.Jobs;

/// <summary>Plage des heures calmes : une file y est-elle retenue, et jusqu'à quand.</summary>
public static class QuietHours
{
    /// <summary>Fin de la plage en cours si la file est retenue à cet instant, sinon null.</summary>
    public static DateTimeOffset? Until(QuietHoursDto q, string lane, DateTimeOffset now)
    {
        if (!q.Enabled || q.Start == q.End || !q.Lanes.Contains(lane)) return null;
        var zone = Zone(q.TimeZone) ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var inside = q.Start < q.End ? time >= q.Start && time < q.End : time >= q.Start || time < q.End;
        if (!inside) return null;
        var end = local.Date + q.End.ToTimeSpan();
        if (q.End <= time) end = end.AddDays(1);
        return new DateTimeOffset(end, zone.GetUtcOffset(end));
    }

    /// <summary>Fuseau d'après son identifiant (IANA, ou Windows converti), null s'il est inconnu.</summary>
    public static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>Heures calmes, modifiables dans l'admin (fichier <c>quiet-hours.json</c> du dossier de données).</summary>
public sealed class QuietHoursStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dir;
    private readonly string _path;
    private readonly Lock _lock = new();
    private QuietHoursDto _settings = QuietHoursDto.Defaults;

    public QuietHoursStore(IOptions<SonNuageOptions> options)
    {
        _dir = options.Value.DataPath;
        _path = Path.Combine(_dir, "quiet-hours.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<QuietHoursDto>(File.ReadAllText(_path), Json) is { } saved)
                _settings = saved;
        }
        catch (JsonException)
        {
        }
    }

    public QuietHoursDto Settings
    {
        get { lock (_lock) return _settings; }
    }

    public DateTimeOffset? Until(string lane, DateTimeOffset now) => QuietHours.Until(Settings, lane, now);

    public void Save(QuietHoursDto s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(s, Json));
            _settings = s;
        }
    }
}
