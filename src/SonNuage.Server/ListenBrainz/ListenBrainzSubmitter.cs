using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.ListenBrainz;

/// <summary>
/// Envoi des écoutes à ListenBrainz, pour les comptes qui l'ont activé : seules les écoutes commencées depuis
/// l'activation, et qui comptent selon ListenBrainz (<see cref="ListenRules.CountsForListenBrainz"/>). Chaque écoute
/// traitée est marquée (envoyée ou écartée) : relancé après une panne, l'envoi reprend où il s'était arrêté.
/// Tourne dans le moteur de tâches (<see cref="ListenBrainzSubmitJob"/>), jamais pendant la lecture.
/// </summary>
public sealed class ListenBrainzSubmitter(IServiceScopeFactory scopes, ListenBrainzApi api, ListenBrainzTokens tokens, TimeProvider time,
    ILogger<ListenBrainzSubmitter> logger)
{
    /// <summary>Écoutes par requête (ListenBrainz en accepte bien plus, mais une petite requête échoue moins).</summary>
    internal const int BatchSize = 100;
    /// <summary>Attente après une panne de ListenBrainz sans délai indiqué.</summary>
    internal static readonly TimeSpan FailureDelay = TimeSpan.FromMinutes(10);

    /// <param name="RetryAt">Il en reste à envoyer, à partir de cette heure (ListenBrainz bride ou ne répond pas).</param>
    public sealed record Run(int Sent, int Ignored, DateTimeOffset? RetryAt);

    private sealed record Account(Guid Id, string? Token, DateTimeOffset Since);

    /// <summary>Requête des écoutes à envoyer d'un compte : celles qui attendent depuis son activation.</summary>
    internal static IQueryable<PlayEvent> Pending(SonNuageDbContext db, Guid userId, DateTimeOffset since) =>
        db.PlayEvents.Where(p => p.UserId == userId && p.ListenBrainz == ListenBrainzSubmission.Pending && p.PlayedAt >= since);

    /// <summary>Y a-t-il quelque chose à envoyer, pour au moins un compte ?</summary>
    public static Task<bool> AnyPendingAsync(SonNuageDbContext db, CancellationToken ct) =>
        db.Users.Where(u => u.ListenBrainzToken != null && u.ListenBrainzSubmitSince != null)
            .AnyAsync(u => db.PlayEvents.Any(p => p.UserId == u.Id && p.ListenBrainz == ListenBrainzSubmission.Pending
                                                  && p.PlayedAt >= u.ListenBrainzSubmitSince), ct);

    public async Task<Run> SubmitPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
        var accounts = await db.Users.Where(u => u.ListenBrainzToken != null && u.ListenBrainzSubmitSince != null)
            .Select(u => new Account(u.Id, u.ListenBrainzToken, u.ListenBrainzSubmitSince!.Value)).ToListAsync(ct);
        int sent = 0, ignored = 0;
        DateTimeOffset? retryAt = null;
        foreach (var account in accounts)
        {
            // Un compte bridé n'empêche pas les autres de partir.
            var (s, i, retry) = await SubmitAccountAsync(db, account, ct);
            sent += s;
            ignored += i;
            if (retry is { } r && (retryAt is null || r < retryAt)) retryAt = r;
        }
        return new Run(sent, ignored, retryAt);
    }

    private async Task<(int Sent, int Ignored, DateTimeOffset? RetryAt)> SubmitAccountAsync(SonNuageDbContext db, Account account, CancellationToken ct)
    {
        if (tokens.Read(account.Token) is not { } token) return (0, 0, null); // clés de chiffrement perdues : jeton à ressaisir
        int sent = 0, ignored = 0;
        DateTimeOffset? waitUntil = null;
        while (true)
        {
            var batch = await Pending(db, account.Id, account.Since).OrderBy(p => p.PlayedAt).ThenBy(p => p.Id).Take(BatchSize)
                .Select(p => new
                {
                    p.Id, p.PlayedAt, p.SecondsPlayed, p.Client, p.Track.Title, p.Track.ArtistName, p.Track.AlbumTitle, p.Track.DurationSeconds,
                    p.Track.MusicBrainzRecordingId, p.Track.MusicBrainzReleaseId, p.Track.TrackNumber,
                })
                .ToListAsync(ct);
            if (batch.Count == 0) return (sent, ignored, null);
            // Plus de requêtes permises pour l'instant : la suite attendra.
            if (waitUntil is { } wait && wait > time.GetUtcNow()) return (sent, ignored, wait);

            var tooShort = batch.Where(p => !ListenRules.CountsForListenBrainz(p.SecondsPlayed, p.DurationSeconds)).Select(p => p.Id).ToList();
            var listens = batch.Where(p => !tooShort.Contains(p.Id)).ToList();
            await MarkAsync(db, tooShort, ListenBrainzSubmission.Ignored, ct);
            ignored += tooShort.Count;
            if (listens.Count == 0) continue;

            var result = await api.SubmitAsync(token, listens.Select(p => new Listen(p.PlayedAt, p.ArtistName, p.Title, p.AlbumTitle,
                p.MusicBrainzRecordingId, p.MusicBrainzReleaseId, p.DurationSeconds, p.TrackNumber, p.Client)).ToList(), ct);
            var now = time.GetUtcNow();
            switch (result.Status)
            {
                case SubmitStatus.Accepted:
                    await MarkAsync(db, listens.Select(p => p.Id).ToList(), ListenBrainzSubmission.Sent, ct);
                    sent += listens.Count;
                    if (result is { Remaining: <= 0, RetryAfter: { } reset }) waitUntil = now + reset;
                    break;
                case SubmitStatus.Rejected:
                    logger.LogWarning("ListenBrainz refuse {Count} écoutes du compte {UserId} : écartées", listens.Count, account.Id);
                    await MarkAsync(db, listens.Select(p => p.Id).ToList(), ListenBrainzSubmission.Ignored, ct);
                    ignored += listens.Count;
                    break;
                case SubmitStatus.Unauthorized:
                    // Jeton révoqué sur ListenBrainz : le compte est délié, l'utilisateur en saisira un nouveau.
                    logger.LogInformation("Jeton ListenBrainz refusé pour le compte {UserId} : compte délié", account.Id);
                    await ListenBrainzTokens.DisconnectAsync(db, account.Id, ct);
                    return (sent, ignored, null);
                default:
                    return (sent, ignored, now + (result.RetryAfter is { } after && after > TimeSpan.Zero ? after : FailureDelay));
            }
        }
    }

    private static Task MarkAsync(SonNuageDbContext db, List<long> ids, ListenBrainzSubmission state, CancellationToken ct) =>
        ids.Count == 0 ? Task.CompletedTask : db.PlayEvents.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.ListenBrainz, state), ct);

    public static JobPlan Plan() =>
        new JobPlan(JobSuiteKinds.ListenBrainzSubmit, Tx.Key("jobs.suites.listenbrainz"))
            .Then(JobKinds.ListenBrainzSubmit, Tx.Key("jobs.suites.listenbrainz"));
}

/// <summary>Envoi des écoutes en attente ; ListenBrainz bridé ou en panne : la tâche revient à l'heure dite.</summary>
public sealed class ListenBrainzSubmitJob(ListenBrainzSubmitter submitter) : IJobHandler
{
    public JobKind Kind => JobKinds.ListenBrainzSubmit;

    public async Task RunAsync(JobContext ctx)
    {
        var run = await submitter.SubmitPendingAsync(ctx.CancellationToken);
        ctx.Info(Tx.Key("jobs.listenbrainz.sent", ("sent", run.Sent), ("ignored", run.Ignored)));
        if (run.RetryAt is { } at) throw new JobDeferredException(at, Tx.Key("jobs.listenbrainz.later"));
    }
}

/// <summary>Toutes les quelques minutes : s'il y a des écoutes à envoyer, la tâche est demandée (une seule à la fois).</summary>
public sealed class ListenBrainzScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<ListenBrainzScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

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
                    logger.LogWarning(ex, "Envoi des écoutes à ListenBrainz non planifié");
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
        using var scope = scopes.CreateScope();
        if (await ListenBrainzSubmitter.AnyPendingAsync(scope.ServiceProvider.GetRequiredService<SonNuageDbContext>(), ct))
            await scopes.RequestAsync(ListenBrainzSubmitter.Plan(), ct);
    }
}
