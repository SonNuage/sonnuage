using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Data;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    /// <summary>File de lecture du compte : enregistrée par une appli, reprise par une autre au même titre, à la même seconde.</summary>
    private static void RegisterQueue()
    {
        On("savePlayQueue", async r =>
        {
            var ids = r.GetAll("id").Select(id => SubsonicIds.Parse(id, SubsonicIds.Track)).OfType<Guid>().Take(SavedQueue.MaxEntries).ToList();
            var known = (await r.Db.Tracks.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(r.Ct)).ToHashSet();
            ids = ids.Where(known.Contains).ToList();

            await using var tx = await r.Db.Database.BeginTransactionAsync(r.Ct);
            await r.Db.SavedQueueEntries.Where(e => e.UserId == r.UserId).ExecuteDeleteAsync(r.Ct);
            await r.Db.SavedQueues.Where(q => q.UserId == r.UserId).ExecuteDeleteAsync(r.Ct);
            if (ids.Count > 0)
            {
                var current = r.Get("current") is { } c ? SubsonicIds.Parse(c, SubsonicIds.Track) : null;
                var client = r.Get("c");
                r.Db.SavedQueues.Add(new SavedQueue
                {
                    UserId = r.UserId,
                    CurrentTrackId = current is { } cur && ids.Contains(cur) ? cur : ids[0],
                    PositionMs = long.TryParse(r.Get("position"), out var ms) && ms > 0 ? ms : 0,
                    ChangedAt = r.Service<TimeProvider>().GetUtcNow(),
                    ChangedBy = string.IsNullOrWhiteSpace(client) ? null : client[..Math.Min(client.Length, SavedQueue.MaxClientLength)],
                    Entries = ids.Select((id, i) => new SavedQueueEntry { UserId = r.UserId, Position = i, TrackId = id }).ToList(),
                });
                await r.Db.SaveChangesAsync(r.Ct);
            }
            await tx.CommitAsync(r.Ct);
            return r.Ok();
        });

        On("getPlayQueue", async r =>
        {
            var root = r.Ok();
            var queue = await r.Db.SavedQueues.AsNoTracking().FirstOrDefaultAsync(q => q.UserId == r.UserId, r.Ct);
            if (queue is null) return root;
            var songs = await r.Db.SavedQueueEntries.Where(e => e.UserId == r.UserId).OrderBy(e => e.Position).Select(e => e.Track)
                .Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
            if (songs.Count == 0) return root;

            var node = root.Add("playQueue")
                .Attr("current", queue.CurrentTrackId is { } current ? SubsonicIds.Of(SubsonicIds.Track, current) : null)
                .Attr("position", queue.PositionMs)
                .Attr("username", r.User.Username)
                .Attr("changed", queue.ChangedAt)
                .Attr("changedBy", queue.ChangedBy);
            var list = node.List("entry");
            foreach (var song in songs) list.Add().Fill(song);
            return root;
        });
    }
}
