using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Api;
using SonNuage.Server.Data;

namespace SonNuage.Server.Youtube;

/// <summary>Un titre demandé depuis une playlist y entre une fois son import terminé.</summary>
public static class RequestedPlaylists
{
    /// <summary>Ajoute le titre importé aux playlists des demandes qui l'attendaient ; rejouer ne change rien.</summary>
    public static async Task AddImportedAsync(SonNuageDbContext db, YoutubeItem item, CancellationToken ct)
    {
        if (item.TrackId is not { } trackId) return;
        var waiting = await db.DownloadRequests
            .Where(r => r.PlaylistId != null && (r.YoutubeItemId == item.Id || r.VideoId == item.VideoId))
            .ToListAsync(ct);
        foreach (var request in waiting)
        {
            if (await db.Playlists.FirstOrDefaultAsync(p => p.Id == request.PlaylistId && p.OwnerId == request.UserId, ct) is { } playlist)
                await PlaylistEndpoints.AddTracksAsync(db, playlist, [trackId], null, ct);
            request.PlaylistId = null;
        }
        if (waiting.Count > 0) await db.SaveChangesAsync(ct);
    }
}
