using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;
using SonNuage.Server.Lyrics;

namespace SonNuage.Server.Api;

/// <summary>Paroles d'un morceau, synchronisées quand c'est possible. 404 s'il n'y en a pas.</summary>
public static class LyricsEndpoints
{
    public static void MapLyricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tracks/{id:guid}/lyrics", async (Guid id, SonNuageDbContext db, LyricsService lyrics, CancellationToken ct) =>
        {
            var track = await db.Tracks.Where(t => t.Id == id)
                .Select(t => new { t.FilePath, Query = new LyricsQuery(t.Title, t.ArtistName, t.AlbumTitle, t.DurationSeconds) })
                .FirstOrDefaultAsync(ct);
            return track is not null && await lyrics.FindAsync(id, track.FilePath, track.Query, ct) is { } found
                ? Results.Ok(found)
                : Results.NotFound();
        }).RequireAuthorization().WithTags("Library");
    }
}
