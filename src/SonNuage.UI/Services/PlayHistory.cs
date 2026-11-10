using System.Net;
using System.Text.Json;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Envoi d'une écoute au serveur (implémenté par <see cref="ApiClient"/>).</summary>
public interface IPlayReportApi
{
    Task ReportPlayAsync(PlayEventRequest play);
}

/// <summary>
/// Historique d'écoute côté appareil. Chaque lecteur (web, ordinateur, Android, iOS) signale ici la fin d'une
/// écoute ; elle part au serveur, ou attend dans une file locale propre au compte tant qu'on est hors ligne, comme
/// les réglages de lecture. La file repart au démarrage, au changement de compte et à chaque nouvelle écoute.
/// Chaque écoute porte un identifiant choisi ici : renvoyée après une réponse perdue, le serveur ne la compte qu'une fois.
/// </summary>
public sealed class PlayHistory
{
    /// <summary>Écoutes gardées hors ligne au plus (les plus anciennes partent d'abord à la poubelle).</summary>
    public const int MaxPending = 500;
    private const string PendingKey = "sonnuage.plays.pending";

    private readonly IKeyValueStore _store;
    private readonly AccountStore _accounts;
    private readonly IPlayReportApi _api;
    private readonly IPlatformInfo _platform;
    // Lecture-modification-écriture de la file, jamais pendant un appel réseau.
    private readonly SemaphoreSlim _list = new(1, 1);
    // Un seul envoi à la fois : la file part dans l'ordre.
    private readonly SemaphoreSlim _flush = new(1, 1);

    public PlayHistory(IKeyValueStore store, AccountStore accounts, IPlayReportApi api, IPlatformInfo platform)
    {
        _store = store;
        _accounts = accounts;
        _api = api;
        _platform = platform;
        accounts.ActiveChanged += () => _ = FlushAsync();
    }

    /// <summary>
    /// Fin d'une écoute (morceau suivant, arrêt, fin de file). Ignorée si elle est trop courte pour compter
    /// (<see cref="ListenRules.CountsAsPlay"/>). Ne lève pas d'erreur réseau : l'écoute attend son tour.
    /// </summary>
    /// <param name="secondsPlayed">Temps réellement écouté (voir <see cref="ListenTracker"/>).</param>
    public async Task ReportAsync(TrackDto track, DateTimeOffset startedAt, double secondsPlayed, bool completed)
    {
        if (!ListenRules.CountsAsPlay(secondsPlayed, track.DurationSeconds)) return;
        var play = new PlayEventRequest(track.Id, Math.Round(secondsPlayed, 1), completed, startedAt, _platform.Name, Guid.NewGuid());
        var key = _accounts.ScopedKey(PendingKey);
        await EditAsync(key, pending =>
        {
            pending.Add(play);
            if (pending.Count > MaxPending) pending.RemoveRange(0, pending.Count - MaxPending);
        });
        await FlushAsync();
    }

    /// <summary>Envoie les écoutes en attente du compte actif, dans l'ordre, jusqu'à la première qui ne passe pas.</summary>
    public async Task FlushAsync()
    {
        await _flush.WaitAsync();
        try
        {
            var account = _accounts.Active?.Id;
            var key = _accounts.ScopedKey(PendingKey);
            // Le compte change en cours de route : la suite partira sous le bon compte, à son tour.
            while (_accounts.Active?.Id == account && (await LoadAsync(key)).FirstOrDefault() is { } next)
            {
                if (!await SendAsync(next)) return;
                await EditAsync(key, pending => pending.RemoveAll(p => p.Id == next.Id));
            }
        }
        finally
        {
            _flush.Release();
        }
    }

    /// <summary>true si l'écoute a quitté la file (reçue, ou refusée pour de bon) ; false s'il faut réessayer plus tard.</summary>
    private async Task<bool> SendAsync(PlayEventRequest play)
    {
        try
        {
            await _api.ReportPlayAsync(play);
            return true;
        }
        catch (ApiException ex) when (IsFinalRefusal(ex.Status))
        {
            return true; // Morceau supprimé entre-temps, requête invalide : la renvoyer n'y changerait rien.
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            return false; // Hors ligne, serveur indisponible, session expirée : plus tard.
        }
    }

    private static bool IsFinalRefusal(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    private async Task<List<PlayEventRequest>> LoadAsync(string key)
    {
        if (await _store.GetAsync(key) is not { Length: > 0 } json) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PlayEventRequest>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task EditAsync(string key, Action<List<PlayEventRequest>> edit)
    {
        await _list.WaitAsync();
        try
        {
            var pending = await LoadAsync(key);
            edit(pending);
            await _store.SetAsync(key, pending.Count == 0 ? null : JsonSerializer.Serialize(pending, JsonDefaults.Options));
        }
        finally
        {
            _list.Release();
        }
    }
}

/// <summary>
/// Temps réellement écouté d'un morceau, d'après les positions successives du lecteur : un saut (avance rapide,
/// retour en arrière) ne compte pas, seule la lecture continue s'ajoute.
/// </summary>
public sealed class ListenTracker(DateTimeOffset startedAt, double position)
{
    // Écart maximal entre deux positions pour une lecture continue (plusieurs par seconde, moins dans un onglet en arrière-plan).
    private const double MaxStep = 10;
    private double _last = position;

    public DateTimeOffset StartedAt { get; } = startedAt;
    public double Seconds { get; private set; }

    public void At(double position)
    {
        var step = position - _last;
        if (step is > 0 and <= MaxStep) Seconds += step;
        _last = position;
    }
}
