using Microsoft.EntityFrameworkCore;
using SonNuage.Server.Library;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    private const string MusicFolderId = "1";

    private static void RegisterSystem()
    {
        On("ping", r => r.Ok());
        On("getLicense", r =>
        {
            var root = r.Ok();
            root.Add("license").Attr("valid", true);
            return root;
        });
        On("getOpenSubsonicExtensions", r =>
        {
            var root = r.Ok();
            var list = root.List("openSubsonicExtensions");
            foreach (var name in new[] { "apiKeyAuthentication", "formPost", "songLyrics", "transcodeOffset" })
                list.Add().Attr("name", name).Values("versions", [1]);
            return root;
        });
        On("getMusicFolders", r =>
        {
            var root = r.Ok();
            root.Add("musicFolders").List("musicFolder").Add().Attr("id", 1).Attr("name", "SonNuage");
            return root;
        });
        On("getScanStatus", ScanStatusAsync);
        On("startScan", ScanStatusAsync);
    }

    private static async Task<object> ScanStatusAsync(SubsonicRequest r)
    {
        var root = r.Ok();
        root.Add("scanStatus").Attr("scanning", r.Service<ScanCoordinator>().Status.Running).Attr("count", await r.Db.Tracks.CountAsync(r.Ct));
        return root;
    }

    private static void RegisterBrowsing()
    {
        On("getIndexes", r => ArtistIndexAsync(r, "indexes"));
        On("getArtists", r => ArtistIndexAsync(r, "artists"));

        On("getArtist", async r =>
        {
            var id = r.RequireId("id", SubsonicIds.Artist, "Artist");
            var artist = await r.Db.Artists.Where(a => a.Id == id).Select(ArtistRows(r.Db, r.UserId)).FirstOrDefaultAsync(r.Ct) ?? throw SubsonicException.NotFound("Artist");
            var root = r.Ok();
            var node = root.Add("artist").Fill(artist);
            var albums = node.List("album");
            foreach (var album in await AlbumsOfAsync(r, id)) albums.Add().Fill(album);
            return root;
        });

        On("getAlbum", async r =>
        {
            var id = r.RequireId("id", SubsonicIds.Album, "Album");
            var album = await r.Db.Albums.Where(a => a.Id == id).Select(Album(r.Db, r.UserId)).FirstOrDefaultAsync(r.Ct) ?? throw SubsonicException.NotFound("Album");
            var root = r.Ok();
            var node = root.Add("album").Fill(album);
            var songs = node.List("song");
            foreach (var song in await SongsOfAlbumAsync(r, id)) songs.Add().Fill(song);
            return root;
        });

        On("getSong", async r =>
        {
            var id = r.RequireId("id", SubsonicIds.Track, "Song");
            var song = await r.Db.Tracks.Where(t => t.Id == id).Select(Song(r.Db, r.UserId)).FirstOrDefaultAsync(r.Ct) ?? throw SubsonicException.NotFound("Song");
            var root = r.Ok();
            root.Add("song").Fill(song);
            return root;
        });

        // Navigation par dossiers (DSub et clients anciens) : un artiste contient ses albums, un album ses morceaux.
        On("getMusicDirectory", async r =>
        {
            var raw = r.Require("id");
            var root = r.Ok();
            if (SubsonicIds.Parse(raw, SubsonicIds.Artist) is { } artistId)
            {
                var artist = await r.Db.Artists.Where(a => a.Id == artistId).Select(ArtistRows(r.Db, r.UserId)).FirstOrDefaultAsync(r.Ct) ?? throw SubsonicException.NotFound("Directory");
                var dir = root.Add("directory").Attr("id", raw).Attr("name", artist.Name);
                var children = dir.List("child");
                foreach (var album in await AlbumsOfAsync(r, artistId)) children.Add().Fill(album, legacy: true);
                return root;
            }
            if (SubsonicIds.Parse(raw, SubsonicIds.Album) is { } albumId)
            {
                var album = await r.Db.Albums.Where(a => a.Id == albumId).Select(Album(r.Db, r.UserId)).FirstOrDefaultAsync(r.Ct) ?? throw SubsonicException.NotFound("Directory");
                var dir = root.Add("directory").Attr("id", raw).Attr("name", album.Title).Attr("parent", SubsonicIds.Of(SubsonicIds.Artist, album.ArtistId));
                var children = dir.List("child");
                foreach (var song in await SongsOfAlbumAsync(r, albumId)) children.Add().Fill(song);
                return root;
            }
            throw SubsonicException.NotFound("Directory");
        });
    }

    /// <summary>Démarrage du serveur : date de la bibliothèque tant qu'aucun scan n'est fini depuis.</summary>
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static async Task<object> ArtistIndexAsync(SubsonicRequest r, string element)
    {
        var root = r.Ok();
        var node = root.Add(element).Attr("ignoredArticles", IgnoredArticles);
        var indexes = node.List("index");
        if (element == "indexes")
        {
            // Date du dernier scan : une appli qui synchronise ne relit tout que si la bibliothèque a changé.
            var lastModified = (r.Service<ScanCoordinator>().Status.LastFinishedAt ?? StartedAt).ToUnixTimeMilliseconds();
            node.Attr("lastModified", lastModified);
            if (long.TryParse(r.Get("ifModifiedSince"), out var since) && since >= lastModified) return root;
        }

        var artists = await r.Db.Artists.Where(a => a.Albums.Any()).OrderBy(a => a.SortName).ThenBy(a => a.Id).Select(ArtistRows(r.Db, r.UserId)).ToListAsync(r.Ct);
        foreach (var group in artists.GroupBy(a => IndexLetter(string.IsNullOrEmpty(a.SortName) ? a.Name : a.SortName)).OrderBy(g => g.Key == "#" ? "~" : g.Key))
        {
            var list = indexes.Add().Attr("name", group.Key).List("artist");
            foreach (var artist in group) list.Add().Fill(artist);
        }
        return root;
    }

    private static Task<List<AlbumRow>> AlbumsOfAsync(SubsonicRequest r, Guid artistId) =>
        r.Db.Albums.Where(a => a.ArtistId == artistId).OrderBy(a => a.Year).ThenBy(a => a.Title).Select(Album(r.Db, r.UserId)).ToListAsync(r.Ct);

    private static Task<List<SongRow>> SongsOfAlbumAsync(SubsonicRequest r, Guid albumId) =>
        r.Db.Tracks.Where(t => t.AlbumId == albumId).OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title)
            .Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
}
