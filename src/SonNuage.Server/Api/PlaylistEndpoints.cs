using SonNuage.Contracts.Localization;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Api;

public static class PlaylistEndpoints
{
    internal const int MaxEntries = 10_000;

    public static void MapPlaylistEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/playlists").RequireAuthorization().WithTags("Playlists");

        g.MapGet("/", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.Playlists.Where(p => p.OwnerId == userId).OrderBy(p => p.Name).Select(Projections.Playlist).ToListAsync(ct);
        });

        // Combien des titres donnés chaque playlist contient déjà : la fenêtre d'ajout le montre (et n'en remet pas).
        g.MapGet("/containing", async ([Microsoft.AspNetCore.Mvc.FromQuery] Guid[] track, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (track.Length is 0 or > MaxEntries) return Results.BadRequest();
            var userId = http.User.UserId();
            var ids = track.Distinct().ToList();
            return Results.Ok(await db.PlaylistEntries
                .Where(e => e.Playlist.OwnerId == userId && ids.Contains(e.TrackId))
                .GroupBy(e => e.PlaylistId)
                .Select(g => new PlaylistContentsDto(g.Key, g.Select(e => e.TrackId).Distinct().Count()))
                .ToListAsync(ct));
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var playlist = await db.Playlists.Where(p => p.Id == id && p.OwnerId == userId).Select(Projections.Playlist).FirstOrDefaultAsync(ct);
            if (playlist is null) return Results.NotFound();
            var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == id).OrderBy(e => e.Position)
                .Select(e => new { e.Id, e.TrackId })
                .ToListAsync(ct);
            var trackDtos = await LibraryEndpoints.TracksInOrderAsync(db, userId, entries.Select(e => e.TrackId).Distinct().ToList(), ct);
            var byId = trackDtos.ToDictionary(t => t.Id);
            var dto = entries.Where(e => byId.ContainsKey(e.TrackId))
                .Select((e, i) => new PlaylistEntryDto(e.Id, i, byId[e.TrackId])).ToList();
            return Results.Ok(new PlaylistDetailDto(playlist, dto));
        });

        g.MapPost("/", async (CreatePlaylistRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem(Tx.Key("errors.nameRequired"), statusCode: 400);
            var playlist = new Playlist { OwnerId = http.User.UserId(), Name = req.Name.Trim(), Description = req.Description?.Trim() };
            db.Playlists.Add(playlist);
            if (req.TrackIds is { Count: > 0 } ids)
            {
                var valid = (await ValidTrackIdsAsync(db, ids, ct)).Distinct();
                playlist.Entries.AddRange(valid.Select((t, i) => new PlaylistEntry { TrackId = t, Position = i }));
            }
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/playlists/{playlist.Id}",
                await db.Playlists.Where(p => p.Id == playlist.Id).Select(Projections.Playlist).FirstAsync(ct));
        });

        g.MapPatch("/{id:guid}", async (Guid id, UpdatePlaylistRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var playlist = await OwnedAsync(db, id, http, ct);
            if (playlist is null) return Results.NotFound();
            if (req.Name is { } name)
            {
                if (string.IsNullOrWhiteSpace(name)) return Results.Problem(Tx.Key("errors.nameRequired"), statusCode: 400);
                playlist.Name = name.Trim();
            }
            if (req.Description is not null) playlist.Description = req.Description.Trim();
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var deleted = await db.Playlists.Where(p => p.Id == id && p.OwnerId == userId).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:guid}/tracks", async (Guid id, AddPlaylistTracksRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var playlist = await OwnedAsync(db, id, http, ct);
            if (playlist is null) return Results.NotFound();
            var result = await AddTracksAsync(db, playlist, req.TrackIds, req.Position, ct);
            return result is null ? Results.Problem(Tx.Key("errors.playlistLimit", ("max", MaxEntries)), statusCode: 400) : Results.Ok(result);
        });

        g.MapDelete("/{id:guid}/entries/{entryId:guid}", async (Guid id, Guid entryId, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var playlist = await OwnedAsync(db, id, http, ct);
            if (playlist is null) return Results.NotFound();
            var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == id).OrderBy(e => e.Position).ToListAsync(ct);
            var entry = entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return Results.NotFound();
            entries.Remove(entry);
            db.PlaylistEntries.Remove(entry);
            Renumber(entries);
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/entries/{entryId:guid}/move", async (Guid id, Guid entryId, MovePlaylistEntryRequest req, HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var playlist = await OwnedAsync(db, id, http, ct);
            if (playlist is null) return Results.NotFound();
            var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == id).OrderBy(e => e.Position).ToListAsync(ct);
            var entry = entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return Results.NotFound();
            entries.Remove(entry);
            entries.Insert(Math.Clamp(req.NewPosition, 0, entries.Count), entry);
            Renumber(entries);
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Ajoute les titres connus qui n'y sont pas encore, une fois chacun, à <paramref name="position"/> (à la fin par
    /// défaut) ; null si la playlist dépasserait sa taille maximale.
    /// </summary>
    internal static async Task<AddPlaylistTracksResultDto?> AddTracksAsync(SonNuageDbContext db, Playlist playlist, IReadOnlyList<Guid> trackIds,
        int? position, CancellationToken ct)
    {
        var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == playlist.Id).OrderBy(e => e.Position).ToListAsync(ct);
        var present = entries.Select(e => e.TrackId).ToHashSet();
        var valid = (await ValidTrackIdsAsync(db, trackIds, ct)).Distinct().ToList();
        var fresh = valid.Where(t => !present.Contains(t)).ToList();
        if (entries.Count + fresh.Count > MaxEntries) return null;
        if (fresh.Count > 0)
        {
            var insertAt = Math.Clamp(position ?? entries.Count, 0, entries.Count);
            var added = fresh.Select(t => new PlaylistEntry { PlaylistId = playlist.Id, TrackId = t }).ToList();
            entries.InsertRange(insertAt, added);
            db.PlaylistEntries.AddRange(added);
            Renumber(entries);
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new AddPlaylistTracksResultDto(fresh.Count, valid.Count - fresh.Count);
    }

    private static void Renumber(List<PlaylistEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++) entries[i].Position = i;
    }

    private static Task<Playlist?> OwnedAsync(SonNuageDbContext db, Guid id, HttpContext http, CancellationToken ct)
    {
        var userId = http.User.UserId();
        return db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId, ct);
    }

    /// <summary>Garde l'ordre voulu par le client, en écartant les IDs inconnus.</summary>
    private static async Task<List<Guid>> ValidTrackIdsAsync(SonNuageDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        var existing = (await db.Tracks.Where(t => distinct.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        return ids.Where(existing.Contains).Take(MaxEntries).ToList();
    }
}
