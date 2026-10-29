using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Localify.Server.Search;

/// <summary>
/// Garde-fou devant Elasticsearch : la recherche ne doit jamais attendre plus que <see cref="SearchOptions.TimeoutMilliseconds"/>.
/// <list type="bullet">
/// <item>Réponse dans le délai : résultat Elasticsearch.</item>
/// <item>Hors délai : l'appelant retombe sur la recherche en base ; la requête continue en arrière-plan
///   (avec nouvelles tentatives) et son résultat est gardé une minute, servi à la prochaine frappe identique.</item>
/// <item>Échecs répétés : Elasticsearch est mis de côté (recherche en base immédiate, sans attendre le délai)
///   et sondé en arrière-plan jusqu'à son retour ; l'index est alors reconstruit.</item>
/// </list>
/// </summary>
public sealed class SearchGateway(ElasticsearchClient es, IOptions<LocalifyOptions> options, TimeProvider time, ILogger<SearchGateway> logger)
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LateResultLifetime = TimeSpan.FromMinutes(1);
    private const int SlowBeforeDegrade = 3;
    private const int MaxLateResults = 500;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, object Value)> _late = new();
    private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new();
    private readonly Lock _probeLock = new();
    private volatile bool _degraded;
    private volatile bool _indexReady;
    private int _consecutiveSlow;
    private Task? _probe;

    /// <summary>Elasticsearch est revenu après une panne : l'index peut avoir été perdu.</summary>
    public event Action? Recovered;

    public bool Enabled => es.Configured;
    public bool Degraded => _degraded;

    /// <summary>Faux tant que l'index n'a jamais été rempli : chercher dans un index vide n'a pas de sens.</summary>
    public bool IndexReady
    {
        get => _indexReady;
        set => _indexReady = value;
    }

    public string Status => !Enabled ? "database" : _degraded ? "degraded" : !_indexReady ? "indexing" : "elasticsearch";

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(Math.Max(100, options.Value.Search.TimeoutMilliseconds));

    /// <summary>Résultat Elasticsearch, ou null si l'appelant doit utiliser la recherche en base.</summary>
    public async Task<T?> TryAsync<T>(string key, Func<CancellationToken, Task<T>> call) where T : class
    {
        if (!Enabled || !_indexReady) return null;
        if (_late.TryGetValue(key, out var late) && late.Expires > time.GetUtcNow()) return (T)late.Value;
        if (_degraded)
        {
            EnsureProbe();
            return null;
        }

        // Une même requête déjà en cours (frappe répétée, deux onglets) n'est pas relancée.
        var task = _inFlight.GetOrAdd(key, k => RunWithRetriesAsync(k, async ct => await call(ct)));
        var finished = await Task.WhenAny(task, Task.Delay(Timeout, time));
        if (finished == task)
        {
            Interlocked.Exchange(ref _consecutiveSlow, 0);
            return (T?)await task;
        }

        logger.LogInformation("Elasticsearch n'a pas répondu en {Timeout} ms : recherche en base, nouvelle tentative en arrière-plan", Timeout.TotalMilliseconds);
        if (Interlocked.Increment(ref _consecutiveSlow) >= SlowBeforeDegrade) MarkDegraded("lenteurs répétées");
        return null;
    }

    /// <summary>Signalé par l'indexeur quand Elasticsearch est injoignable.</summary>
    public void ReportFailure(Exception error) => MarkDegraded(error.Message);

    private async Task<object?> RunWithRetriesAsync(string key, Func<CancellationToken, Task<object>> call)
    {
        try
        {
            Exception? last = null;
            foreach (var delay in RetryDelays)
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, time);
                try
                {
                    using var cts = new CancellationTokenSource(AttemptTimeout, time);
                    var result = await call(cts.Token);
                    Remember(key, result);
                    return result;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    last = ex;
                }
            }
            MarkDegraded(last?.Message ?? "échec");
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private void Remember(string key, object value)
    {
        if (_late.Count >= MaxLateResults)
        {
            var now = time.GetUtcNow();
            foreach (var (k, v) in _late)
            {
                if (v.Expires <= now) _late.TryRemove(k, out _);
            }
            if (_late.Count >= MaxLateResults) _late.Clear();
        }
        _late[key] = (time.GetUtcNow() + LateResultLifetime, value);
    }

    private void MarkDegraded(string reason)
    {
        if (!_degraded) logger.LogWarning("Elasticsearch mis de côté ({Reason}) : recherche en base jusqu'à son retour", reason);
        _degraded = true;
        EnsureProbe();
    }

    private void EnsureProbe()
    {
        lock (_probeLock)
        {
            if (_probe is { IsCompleted: false }) return;
            _probe = Task.Run(ProbeAsync);
        }
    }

    private async Task ProbeAsync()
    {
        var delay = TimeSpan.FromSeconds(5);
        while (_degraded)
        {
            await Task.Delay(delay, time);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
                await es.PingAsync(cts.Token);
                _degraded = false;
                Interlocked.Exchange(ref _consecutiveSlow, 0);
                _late.Clear();
                logger.LogInformation("Elasticsearch de nouveau joignable");
                Recovered?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }
}
