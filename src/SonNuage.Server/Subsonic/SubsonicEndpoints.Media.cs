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

        // Note de 1 à 5 sur un titre, un album ou un artiste ; 0 la retire.
        On("setRating", async r =>
        {
            var raw = r.Require("id");
            var rating = r.IntOrNull("rating") ?? throw new SubsonicException(SubsonicError.MissingParameter, "Required parameter is missing: rating");
            if (rating is < 0 or > UserRating.Max) throw new SubsonicException(SubsonicError.Generic, "Rating must be between 0 and 5");
            Guid? id = SubsonicIds.Parse(raw, SubsonicIds.Album) is { } album && await r.Db.Albums.AnyAsync(a => a.Id == album, r.Ct) ? album
                : SubsonicIds.Parse(raw, SubsonicIds.Artist) is { } artist && await r.Db.Artists.AnyAsync(a => a.Id == artist, r.Ct) ? artist
                : SubsonicIds.Parse(raw, SubsonicIds.Track) is { } track && await r.Db.Tracks.AnyAsync(t => t.Id == track, r.Ct) ? track
                : null;
            if (id is not { } itemId) throw SubsonicException.NotFound("Item");

            await UserRatings.SetAsync(r.Db, r.UserId, itemId, rating, r.Service<TimeProvider>().GetUtcNow(), r.Ct);
            return r.Ok();
        });

        On("star", r => StarAsync(r, star: true));
        On("unstar", r => StarAsync(r, star: false));

        // submission=false : « en cours de lecture », non retenu. Sinon une écoute complète, à l'heure donnée (ms).
        On("scrobble", async r =>
        {
            var ids = r.GetAll("id").Select(id => SubsonicIds.Parse(id, SubsonicIds.Track)).OfType<Guid>().ToList();
            var durations = await r.Db.Tracks.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.DurationSeconds, r.Ct);
            var board = r.Service<NowPlayingBoard>();
            if (!r.Bool("submission", true))
            {
                if (ids.LastOrDefault(durations.ContainsKey) is var playing && playing != Guid.Empty)
                    board.Start(r.UserId, r.User.Username, r.Get("c"), playing, durations[playing]);
                return r.Ok();
            }
            foreach (var id in ids) board.Finish(r.UserId, r.Get("c"), id);
            var times = r.GetAll("time");
            var now = r.Service<TimeProvider>().GetUtcNow();
            var client = Api.ListenHistory.Client(r.Get("c"));
            for (var i = 0; i < ids.Count; i++)
            {
                if (!durations.TryGetValue(ids[i], out var duration)) continue;
                var playedAt = i < times.Count && long.TryParse(times[i], out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : now;
                r.Db.PlayEvents.Add(new PlayEvent { UserId = r.UserId, TrackId = ids[i], PlayedAt = playedAt, SecondsPlayed = duration, Completed = true, Client = client });
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
            return await r.Db.Artists.Where(a => a.Id == artist).Select(ArtistRows(r.Db, r.UserId)).Select(a => a.CoverAlbumId).FirstOrDefaultAsync(r.Ct);
        if (SubsonicIds.Parse(id, SubsonicIds.Playlist) is { } playlist)
            return StationOfPlaylist(playlist) is not null
                ? (await StationPlaylistsAsync(r)).FirstOrDefault(p => p.Id == playlist)?.CoverAlbumId
                : await r.Db.Playlists.Where(p => p.Id == playlist).Select(PlaylistRows).Select(p => p.CoverAlbumId).FirstOrDefaultAsync(r.Ct);
        if (SubsonicIds.Parse(id, SubsonicIds.Track) is { } track)
            return await r.Db.Tracks.Where(t => t.Id == track).Select(t => (Guid?)t.AlbumId).FirstOrDefaultAsync(r.Ct);
        return null;
    }

    /// <summary>
    /// Favoris : titres (<c>id</c>), albums (<c>albumId</c>, ou <c>id</c> d'un album chez les anciens clients) et
    /// artistes (<c>artistId</c>, ou <c>id</c> d'un artiste).
    /// </summary>
    private static async Task<object> StarAsync(SubsonicRequest r, bool star)
    {
        var raw = r.GetAll("id");
        var tracks = raw.Select(id => SubsonicIds.Parse(id, SubsonicIds.Track)).OfType<Guid>().Distinct().ToList();
        var albums = raw.Concat(r.GetAll("albumId")).Select(id => SubsonicIds.Parse(id, SubsonicIds.Album)).OfType<Guid>().Distinct().ToList();
        var artists = raw.Concat(r.GetAll("artistId")).Select(id => SubsonicIds.Parse(id, SubsonicIds.Artist)).OfType<Guid>().Distinct().ToList();
        var uid = r.UserId;

        var existingTracks = await r.Db.Tracks.Where(t => tracks.Contains(t.Id)).Select(t => t.Id).ToListAsync(r.Ct);
        var existingAlbums = await r.Db.Albums.Where(a => albums.Contains(a.Id)).Select(a => a.Id).ToListAsync(r.Ct);
        var existingArtists = await r.Db.Artists.Where(a => artists.Contains(a.Id)).Select(a => a.Id).ToListAsync(r.Ct);
        var trackFavs = await r.Db.Favorites.Where(f => f.UserId == uid && existingTracks.Contains(f.TrackId)).ToListAsync(r.Ct);
        var albumFavs = await r.Db.FavoriteAlbums.Where(f => f.UserId == uid && existingAlbums.Contains(f.AlbumId)).ToListAsync(r.Ct);
        var artistFavs = await r.Db.FavoriteArtists.Where(f => f.UserId == uid && existingArtists.Contains(f.ArtistId)).ToListAsync(r.Ct);
        if (star)
        {
            r.Db.Favorites.AddRange(existingTracks.Except(trackFavs.Select(f => f.TrackId)).Select(id => new Favorite { UserId = uid, TrackId = id }));
            r.Db.FavoriteAlbums.AddRange(existingAlbums.Except(albumFavs.Select(f => f.AlbumId)).Select(id => new FavoriteAlbum { UserId = uid, AlbumId = id }));
            r.Db.FavoriteArtists.AddRange(existingArtists.Except(artistFavs.Select(f => f.ArtistId)).Select(id => new FavoriteArtist { UserId = uid, ArtistId = id }));
        }
        else
        {
            r.Db.Favorites.RemoveRange(trackFavs);
            r.Db.FavoriteAlbums.RemoveRange(albumFavs);
            r.Db.FavoriteArtists.RemoveRange(artistFavs);
        }
        await r.Db.SaveChangesAsync(r.Ct);
        return r.Ok();
    }
}
