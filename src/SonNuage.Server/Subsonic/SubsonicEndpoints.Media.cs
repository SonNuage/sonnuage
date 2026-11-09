using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Streaming;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    private static void RegisterMedia()
    {
        On("stream", async r =>
        {
            var track = await TrackAsync(r);
            var start = r.IntOrNull("timeOffset") is > 0 and var offset ? (double?)offset : null;
            return await StreamEndpoints.ServeAsync(track, Quality(r.Get("format"), r.IntOrNull("maxBitRate"), track),
                start, r.Http, r.Service<TranscodeService>(), r.Ct);
        });

        On("download", async r =>
        {
            var track = await TrackAsync(r);
            return Results.File(track.FilePath, LibraryFiles.ContentType(track.FilePath), Path.GetFileName(track.FilePath),
                lastModified: track.FileModifiedUtc, enableRangeProcessing: true);
        });

        On("getCoverArt", async r =>
        {
            var albumId = await CoverAlbumAsync(r, r.Require("id")) ?? throw SubsonicException.NotFound("Cover art");
            var album = await r.Db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.Id == albumId, r.Ct) ?? throw SubsonicException.NotFound("Cover art");
            var cover = await r.Service<CoverService>().GetAsync(r.Db, album, r.IntOrNull("size"), r.Ct) ?? throw SubsonicException.NotFound("Cover art");
            r.Http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(cover.Path, cover.ContentType, enableRangeProcessing: false);
        });

        On("star", r => StarAsync(r, star: true));
        On("unstar", r => StarAsync(r, star: false));

        // submission=false : « en cours de lecture », non retenu. Sinon une écoute complète, à l'heure donnée (ms).
        On("scrobble", async r =>
        {
            if (!r.Bool("submission", true)) return r.Ok();
            var ids = r.GetAll("id").Select(id => SubsonicIds.Parse(id, SubsonicIds.Track)).OfType<Guid>().ToList();
            var times = r.GetAll("time");
            var durations = await r.Db.Tracks.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.DurationSeconds, r.Ct);
            var now = r.Service<TimeProvider>().GetUtcNow();
            for (var i = 0; i < ids.Count; i++)
            {
                if (!durations.TryGetValue(ids[i], out var duration)) continue;
                var playedAt = i < times.Count && long.TryParse(times[i], out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : now;
                r.Db.PlayEvents.Add(new PlayEvent { UserId = r.UserId, TrackId = ids[i], PlayedAt = playedAt, SecondsPlayed = duration, Completed = true });
            }
            await r.Db.SaveChangesAsync(r.Ct);
            return r.Ok();
        });
    }

    private static async Task<Track> TrackAsync(SubsonicRequest r)
    {
        var id = r.RequireId("id", SubsonicIds.Track, "Song");
        var track = await r.Db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, r.Ct);
        return track is not null && File.Exists(track.FilePath) ? track : throw SubsonicException.NotFound("Song");
    }

    /// <summary>
    /// <c>format=raw</c> ou rien : le fichier d'origine. <c>maxBitRate</c> : le meilleur MP3 qui tient dedans (l'original
    /// s'il est déjà plus léger). <c>format=flac</c> : sans perte. <c>format=mp3</c> seul : 320 kbit/s.
    /// </summary>
    internal static StreamQuality Quality(string? format, int? maxBitRate, Track track)
    {
        if (format is "raw") return StreamQuality.Original;
        if (format is "flac") return StreamQuality.Lossless;
        if (maxBitRate is > 0 and var max)
        {
            if (!track.IsLossless && track.BitrateKbps is { } br && br <= max) return StreamQuality.Original;
            return max >= 320 ? StreamQuality.High : max >= 192 ? StreamQuality.Normal : max >= 128 ? StreamQuality.Low : StreamQuality.DataSaver;
        }
        return format is "mp3" ? StreamQuality.High : StreamQuality.Original;
    }

    private static async Task<Guid?> CoverAlbumAsync(SubsonicRequest r, string id)
    {
        if (SubsonicIds.Parse(id, SubsonicIds.Album) is { } album) return album;
        if (SubsonicIds.Parse(id, SubsonicIds.Artist) is { } artist)
            return await r.Db.Artists.Where(a => a.Id == artist).Select(ArtistRows).Select(a => a.CoverAlbumId).FirstOrDefaultAsync(r.Ct);
        if (SubsonicIds.Parse(id, SubsonicIds.Playlist) is { } playlist)
            return await r.Db.Playlists.Where(p => p.Id == playlist).Select(PlaylistRows).Select(p => p.CoverAlbumId).FirstOrDefaultAsync(r.Ct);
        if (SubsonicIds.Parse(id, SubsonicIds.Track) is { } track)
            return await r.Db.Tracks.Where(t => t.Id == track).Select(t => (Guid?)t.AlbumId).FirstOrDefaultAsync(r.Ct);
        return null;
    }

    /// <summary>Favoris : SonNuage ne retient que les morceaux (<c>id</c>). Les albums et artistes sont acceptés sans effet.</summary>
    private static async Task<object> StarAsync(SubsonicRequest r, bool star)
    {
        var ids = r.GetAll("id").Select(id => SubsonicIds.Parse(id, SubsonicIds.Track)).OfType<Guid>().Distinct().ToList();
        var existing = await r.Db.Tracks.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(r.Ct);
        var favorites = await r.Db.Favorites.Where(f => f.UserId == r.UserId && existing.Contains(f.TrackId)).ToListAsync(r.Ct);
        if (star)
        {
            foreach (var id in existing.Except(favorites.Select(f => f.TrackId)))
                r.Db.Favorites.Add(new Favorite { UserId = r.UserId, TrackId = id });
        }
        else r.Db.Favorites.RemoveRange(favorites);
        await r.Db.SaveChangesAsync(r.Ct);
        return r.Ok();
    }
}
