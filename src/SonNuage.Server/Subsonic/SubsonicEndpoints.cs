using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Library;

namespace SonNuage.Server.Subsonic;

/// <summary>
/// API Subsonic / OpenSubsonic sous <c>/rest/*</c> (avec ou sans <c>.view</c>, GET ou formulaire POST), branchée sur
/// les services existants : ouvre SonNuage à Symfonium, Feishin, DSub, Substreamer, Tempo… L'API <c>/api/*</c> des
/// applis SonNuage ne change pas.
/// </summary>
public static partial class SubsonicEndpoints
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(5);

    private delegate Task<object> Handler(SubsonicRequest r);

    /// <summary>Accessibles sans identifiants (OpenSubsonic l'exige pour la liste des extensions).</summary>
    private static readonly HashSet<string> Anonymous = new(StringComparer.OrdinalIgnoreCase) { "getOpenSubsonicExtensions" };

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.OrdinalIgnoreCase);

    static SubsonicEndpoints()
    {
        RegisterSystem();
        RegisterBrowsing();
        RegisterLists();
        RegisterMedia();
        RegisterUser();
        RegisterEmpty();
        RegisterLyrics();
        RegisterQueue();
    }

    private static void On(string method, Func<SubsonicRequest, Task<object>> handler) => Handlers[method] = new Handler(handler);

    private static void On(string method, Func<SubsonicRequest, object> handler) => Handlers[method] = r => Task.FromResult(handler(r));

    public static void MapSubsonicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/rest/{method}", ["GET", "POST"], (string method, HttpContext http) => DispatchAsync(method, http))
            .AllowAnonymous().WithTags("Subsonic");
    }

    private static async Task DispatchAsync(string method, HttpContext http)
    {
        var name = method.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ? method[..^5] : method;
        var request = new SubsonicRequest(http, await ReadParametersAsync(http.Request), AppVersion.Of(typeof(SubsonicEndpoints).Assembly));
        object result;
        try
        {
            if (!Anonymous.Contains(name)) request.User = await AuthenticateAsync(request);
            result = Handlers.TryGetValue(name, out var handler)
                ? await handler(request)
                : throw new SubsonicException(SubsonicError.Generic, $"Unknown method: {name}");
        }
        catch (SubsonicException e)
        {
            result = SubsonicNode.Error(request.ServerVersion, e.Code, e.Message);
        }

        if (result is IResult file)
        {
            await file.ExecuteAsync(http);
            return;
        }
        await WriteAsync(http, request, (SubsonicNode)result);
    }

    private static async Task<Dictionary<string, List<string>>> ReadParametersAsync(HttpRequest request)
    {
        var parameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, IEnumerable<string?> values)
        {
            if (!parameters.TryGetValue(key, out var list)) parameters[key] = list = [];
            list.AddRange(values.OfType<string>());
        }
        foreach (var (key, values) in request.Query) Add(key, values);
        if (request.HasFormContentType)
            foreach (var (key, values) in await request.ReadFormAsync(request.HttpContext.RequestAborted)) Add(key, values);
        return parameters;
    }

    private static async Task WriteAsync(HttpContext http, SubsonicRequest request, SubsonicNode response)
    {
        http.Response.Headers.CacheControl = "no-store";
        switch (request.Get("f"))
        {
            case "json":
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(response.ToJson(), http.RequestAborted);
                break;
            case "jsonp" when request.Get("callback") is { } callback && System.Text.RegularExpressions.Regex.IsMatch(callback, "^[A-Za-z_$][A-Za-z0-9_$.]*$"):
                http.Response.ContentType = "application/javascript; charset=utf-8";
                await http.Response.WriteAsync($"{callback}({response.ToJson()});", http.RequestAborted);
                break;
            default:
                http.Response.ContentType = "text/xml; charset=utf-8";
                await http.Response.WriteAsync(response.ToXml(), http.RequestAborted);
                break;
        }
    }

    // ---- Authentification ----

    /// <summary>
    /// Jeton (<c>u</c>, <c>t</c>, <c>s</c>), mot de passe (<c>u</c>, <c>p</c>) ou clé d'API OpenSubsonic (<c>apiKey</c>),
    /// toujours avec le mot de passe Subsonic du compte. Au-delà de 10 échecs en 5 minutes, l'adresse est refusée.
    /// </summary>
    private static async Task<Data.User> AuthenticateAsync(SubsonicRequest r)
    {
        var cache = r.Service<IMemoryCache>();
        var key = "subsonic-fail:" + (r.Http.Connection.RemoteIpAddress?.ToString() ?? "?");
        var failures = cache.TryGetValue(key, out int count) ? count : 0;
        if (failures >= MaxFailures)
            throw new SubsonicException(SubsonicError.WrongCredentials, "Too many failed attempts, try again in a few minutes");
        try
        {
            var user = await CheckCredentialsAsync(r);
            await NoteSeenAsync(r, user);
            return user;
        }
        catch (SubsonicException e) when (e.Code is SubsonicError.WrongCredentials or SubsonicError.InvalidApiKey)
        {
            cache.Set(key, failures + 1, FailureWindow);
            throw;
        }
    }

    private static async Task<Data.User> CheckCredentialsAsync(SubsonicRequest r)
    {
        var apiKey = r.Get("apiKey");
        var username = r.Get("u");
        if (apiKey is not null)
        {
            if (username is not null) throw new SubsonicException(SubsonicError.ConflictingAuth, "Provide either u or apiKey, not both");
            return await SubsonicPasswords.FindByKeyAsync(r.Db, apiKey, r.Ct)
                ?? throw new SubsonicException(SubsonicError.InvalidApiKey, "Invalid API key");
        }
        if (string.IsNullOrEmpty(username)) throw new SubsonicException(SubsonicError.MissingParameter, "Required parameter is missing: u");
        var (token, salt, password) = (r.Get("t"), r.Get("s"), r.Get("p"));
        if (token is null && password is null) throw new SubsonicException(SubsonicError.MissingParameter, "Required parameter is missing: t or p");

        var normalized = UserService.Normalize(username);
        var user = await r.Db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, r.Ct);
        var expected = user is null ? null : r.Service<SubsonicPasswords>().Read(user);
        var valid = expected is not null && (token is not null
            ? salt is not null && SubsonicCredentials.TokenMatches(expected, salt, token)
            : SubsonicCredentials.DecodePassword(password!) is { } given && SubsonicCredentials.PasswordMatches(expected, given));
        return valid ? user! : throw new SubsonicException(SubsonicError.WrongCredentials, "Wrong username or password");
    }

    /// <summary>« Dernière connexion » de l'admin : aussi pour les applis Subsonic, au plus toutes les 5 minutes.</summary>
    private static async Task NoteSeenAsync(SubsonicRequest r, Data.User user)
    {
        var now = r.Service<TimeProvider>().GetUtcNow();
        if (user.LastSeenAt is { } seen && now - seen < SeenEvery) return;
        user.LastSeenAt = now;
        var client = r.Get("c");
        user.LastSeenDevice = string.IsNullOrWhiteSpace(client) ? "Subsonic" : $"Subsonic · {client[..Math.Min(client.Length, 80)]}";
        await r.Db.SaveChangesAsync(r.Ct);
    }
}
