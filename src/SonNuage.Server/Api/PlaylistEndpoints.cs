using SonNuage.Contracts.Localization;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Api;

public static class PlaylistEndpoints
{
    private const int MaxEntries = 10_000;

    public static void MapPlaylistEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/playlists").RequireAuthorization().WithTags("Playlists");

        g.MapGet("/", async (HttpContext http, SonNuageDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.Playlists.Where(p => p.OwnerId == userId).OrderBy(p => p.Name).Select(Projections.Playlist).ToListAsync(ct);
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
                var valid = await ValidTrackIdsAsync(db, ids, ct);
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
            var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == id).OrderBy(e => e.Position).ToListAsync(ct);
            var valid = await ValidTrackIdsAsync(db, req.TrackIds, ct);
            if (entries.Count + valid.Count > MaxEntries) return Results.Problem(Tx.Key("errors.playlistLimit", ("max", MaxEntries)), statusCode: 400);

            var insertAt = Math.Clamp(req.Position ?? entries.Count, 0, entries.Count);
            var added = valid.Select(t => new PlaylistEntry { PlaylistId = id, TrackId = t }).ToList();
            entries.InsertRange(insertAt, added);
            db.PlaylistEntries.AddRange(added);
            Renumber(entries);
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
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

    private static void Renumber(List<PlaylistEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++) entries[i].Position = i;
    }

    private static Task<Playlist?> OwnedAsync(SonNuageDbContext db, Guid id, HttpContext http, CancellationToken ct)
    {
        var userId = http.User.UserId();
        return db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId, ct);
    }

    /// <summary>Garde l'ordre et les doublons voulus par le client, en écartant les IDs inconnus.</summary>
    private static async Task<List<Guid>> ValidTrackIdsAsync(SonNuageDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        var existing = (await db.Tracks.Where(t => distinct.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        return ids.Where(existing.Contains).Take(MaxEntries).ToList();
    }
}
