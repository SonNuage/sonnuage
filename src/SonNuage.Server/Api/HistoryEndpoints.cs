using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;

namespace SonNuage.Server.Api;

/// <summary>
/// Historique d'écoute de chaque compte : écoutes envoyées par les applis (et les applis Subsonic, voir
/// <see cref="ListenHistory"/>), page Historique, statistiques et effacement. Personne d'autre ne le voit.
/// </summary>
public static class HistoryEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxDays = 3650;
    private const int TopCount = 10;

    public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me").RequireAuthorization().WithTags("Me");

        g.MapPost("/plays", async (PlayEventRequest req, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
            await ListenHistory.RecordAsync(db, http.User.UserId(), req, time.GetUtcNow(), ct) ? Results.NoContent() : Results.NotFound());

        g.MapGet("/history", async (int? offset, int? limit, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var o = Math.Max(0, offset ?? 0);
            var l = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var mine = db.PlayEvents.Where(p => p.UserId == userId);
            var total = await mine.CountAsync(ct);
            var page = await mine.OrderByDescending(p => p.PlayedAt).ThenByDescending(p => p.Id).Skip(o).Take(l)
                .Select(p => new { p.Id, p.TrackId, p.PlayedAt, p.SecondsPlayed, p.Completed, p.Client }).ToListAsync(ct);
            var tracks = (await LibraryEndpoints.TracksInOrderAsync(db, userId, page.Select(p => p.TrackId).Distinct().ToList(), ct))
                .ToDictionary(t => t.Id);
            var items = page.Select(p => new HistoryEntryDto(p.Id, tracks[p.TrackId], p.PlayedAt, p.SecondsPlayed, p.Completed, p.Client)).ToList();
            return new PagedResult<HistoryEntryDto>(items, total, o, l);
        });

        g.MapGet("/history/stats", async (int? days, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var d = days ?? 30;
            if (d is < 0 or > MaxDays) return Results.BadRequest();
            return Results.Ok(await StatsAsync(db, http.User.UserId(), d, time.GetUtcNow(), ct));
        });

        g.MapDelete("/history/{id:long}", async (long id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var deleted = await db.PlayEvents.Where(p => p.Id == id && p.UserId == userId).ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        g.MapDelete("/history", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.PlayEvents.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<HistoryStatsDto> StatsAsync(SonNuageDbContext db, Guid userId, int days, DateTimeOffset now, CancellationToken ct)
    {
        var mine = db.PlayEvents.Where(p => p.UserId == userId);
        var weekStart = now.AddDays(-7);
        var monthStart = now.AddDays(-30);
        var week = await mine.Where(p => p.PlayedAt >= weekStart).SumAsync(p => p.SecondsPlayed, ct);
        var month = await mine.Where(p => p.PlayedAt >= monthStart).SumAsync(p => p.SecondsPlayed, ct);
        var periodStart = now.AddDays(-days);
        var period = days == 0 ? mine : mine.Where(p => p.PlayedAt >= periodStart);
        var plays = await period.CountAsync(ct);

        // À égalité, le plus récemment écouté d'abord.
        var topTracks = await period.GroupBy(p => p.TrackId)
            .Select(x => new { x.Key, Plays = x.Count(), Last = x.Max(p => p.PlayedAt) })
            .OrderByDescending(x => x.Plays).ThenByDescending(x => x.Last).Take(TopCount).ToListAsync(ct);
        var trackDtos = (await LibraryEndpoints.TracksInOrderAsync(db, userId, topTracks.Select(t => t.Key).ToList(), ct)).ToDictionary(t => t.Id);

        var topArtists = await period.GroupBy(p => p.Track.ArtistId)
            .Select(x => new { x.Key, Plays = x.Count(), Last = x.Max(p => p.PlayedAt) })
            .OrderByDescending(x => x.Plays).ThenByDescending(x => x.Last).Take(TopCount).ToListAsync(ct);
        var artistIds = topArtists.Select(a => a.Key).ToList();
        var artistDtos = await db.Artists.Where(a => artistIds.Contains(a.Id)).Select(Projections.Artist).ToDictionaryAsync(a => a.Id, ct);

        return new HistoryStatsDto(days, week, month, plays,
            topArtists.Where(a => artistDtos.ContainsKey(a.Key)).Select(a => new HistoryTopArtistDto(artistDtos[a.Key], a.Plays)).ToList(),
            topTracks.Where(t => trackDtos.ContainsKey(t.Key)).Select(t => new HistoryTopTrackDto(trackDtos[t.Key], t.Plays)).ToList());
    }
}

/// <summary>Écriture de l'historique, commune à l'API des applis et au <c>scrobble</c> des applis Subsonic.</summary>
public static class ListenHistory
{
    /// <summary>Au-delà, une durée envoyée n'a pas de sens (lecteur resté en boucle…).</summary>
    private const double MaxSecondsPlayed = 24 * 3600;

    /// <summary>Enregistre une écoute ; false si le morceau n'existe pas. Une écoute déjà reçue (même identifiant) est ignorée.</summary>
    public static async Task<bool> RecordAsync(SonNuageDbContext db, Guid userId, PlayEventRequest req, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Tracks.AnyAsync(t => t.Id == req.TrackId, ct)) return false;
        if (req.Id is { } id && await db.PlayEvents.AnyAsync(p => p.UserId == userId && p.ClientEventId == id, ct)) return true;
        var seconds = double.IsFinite(req.SecondsPlayed) ? Math.Clamp(req.SecondsPlayed, 0, MaxSecondsPlayed) : 0;
        db.PlayEvents.Add(new PlayEvent
        {
            UserId = userId,
            TrackId = req.TrackId,
            // Pas d'heure de début (ancienne appli) ou une horloge en avance : on la déduit de l'heure de réception.
            PlayedAt = req.StartedAt is { } started && started <= now ? started : now.AddSeconds(-seconds),
            SecondsPlayed = seconds,
            Completed = req.Completed,
            Client = Client(req.Client),
            ClientEventId = req.Id,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (req.Id is not null)
        {
            // La même écoute, arrivée deux fois en même temps : la première suffit.
            db.ChangeTracker.Clear();
        }
        return true;
    }

    public static string? Client(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim() is var c && c.Length > PlayEvent.MaxClientLength ? c[..PlayEvent.MaxClientLength] : c;
}
