using System.Globalization;
using SonNuage.Server.Data;

namespace SonNuage.Server.Subsonic;

/// <summary>Erreur rendue au client Subsonic (réponse <c>status="failed"</c> avec son code).</summary>
public sealed class SubsonicException(SubsonicError code, string message) : Exception(message)
{
    public SubsonicError Code { get; } = code;

    public static SubsonicException NotFound(string what) => new(SubsonicError.NotFound, $"{what} not found");
}

/// <summary>
/// Identifiants Subsonic : préfixe du type puis l'identifiant SonNuage (<c>ar-</c>, <c>al-</c>, <c>tr-</c>, <c>pl-</c>).
/// Les clients les traitent comme des chaînes opaques et les renvoient tels quels.
/// </summary>
public static class SubsonicIds
{
    public const string Artist = "ar-";
    public const string Album = "al-";
    public const string Track = "tr-";
    public const string Playlist = "pl-";

    public static string Of(string prefix, Guid id) => prefix + id.ToString("N");

    /// <summary>Identifiant du type voulu, ou null. Un GUID nu vaut pour un morceau.</summary>
    public static Guid? Parse(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith(prefix, StringComparison.Ordinal))
            return Guid.TryParse(value.AsSpan(prefix.Length), out var id) ? id : null;
        return prefix == Track && Guid.TryParse(value, out var raw) ? raw : null;
    }
}

/// <summary>Une requête <c>/rest/*</c> : paramètres de la query et du formulaire (POST), compte authentifié.</summary>
public sealed class SubsonicRequest(HttpContext http, IReadOnlyDictionary<string, List<string>> parameters, string serverVersion)
{
    public HttpContext Http { get; } = http;
    public string ServerVersion { get; } = serverVersion;
    public User User { get; set; } = null!;
    public Guid UserId => User.Id;
    public SonNuageDbContext Db => Service<SonNuageDbContext>();
    public CancellationToken Ct => Http.RequestAborted;

    public T Service<T>() where T : notnull => Http.RequestServices.GetRequiredService<T>();

    public SubsonicNode Ok() => SubsonicNode.Ok(ServerVersion);

    public string? Get(string name) => parameters.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public IReadOnlyList<string> GetAll(string name) => parameters.TryGetValue(name, out var values) ? values : [];

    public string Require(string name) =>
        Get(name) is { Length: > 0 } value ? value : throw new SubsonicException(SubsonicError.MissingParameter, $"Required parameter is missing: {name}");

    public int Int(string name, int fallback, int min = 0, int max = int.MaxValue) =>
        Math.Clamp(IntOrNull(name) ?? fallback, min, max);

    public int? IntOrNull(string name) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public bool Bool(string name, bool fallback) => Get(name) is { } v && bool.TryParse(v, out var b) ? b : fallback;

    public Guid RequireId(string name, string prefix, string what) =>
        SubsonicIds.Parse(Require(name), prefix) ?? throw SubsonicException.NotFound(what);
}
