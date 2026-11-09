using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    private const int MaxPage = 500;

    private static void RegisterLists()
    {
        On("getAlbumList2", r => AlbumListAsync(r, "albumList2", legacy: false));
        On("getAlbumList", r => AlbumListAsync(r, "albumList", legacy: true));

        On("getRandomSongs", async r =>
        {
            var query = FilterSongs(r.Db.Tracks.AsQueryable(), r.Get("genre"), r.IntOrNull("fromYear"), r.IntOrNull("toYear"));
            var songs = await query.OrderBy(_ => EF.Functions.Random()).Take(r.Int("size", 10, 1, MaxPage)).Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
            return SongList(r, "randomSongs", songs);
        });

        On("getSongsByGenre", async r =>
        {
            var genre = r.Require("genre");
            var songs = await r.Db.Tracks.Where(t => t.Genre == genre).OrderBy(t => t.ArtistName).ThenBy(t => t.AlbumTitle).ThenBy(t => t.TrackNumber).ThenBy(t => t.Id)
                .Skip(r.Int("offset", 0)).Take(r.Int("count", 10, 1, MaxPage)).Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
            return SongList(r, "songsByGenre", songs);
        });

        On("getGenres", async r =>
        {
            var pairs = await r.Db.Tracks.Where(t => t.Genre != null && t.Genre != "").Select(t => new { t.Genre, t.AlbumId }).ToListAsync(r.Ct);
            var root = r.Ok();
            var list = root.Add("genres").List("genre");
            foreach (var g in pairs.GroupBy(p => p.Genre!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                list.Add().Attr("songCount", g.Count()).Attr("albumCount", g.Select(p => p.AlbumId).Distinct().Count()).Text(g.Key);
            return root;
        });

        On("search3", r => SearchAsync(r, "searchResult3", legacy: false));
        On("search2", r => SearchAsync(r, "searchResult2", legacy: true));

        On("getStarred2", r => StarredAsync(r, "starred2"));
        On("getStarred", r => StarredAsync(r, "starred"));

        On("getSimilarSongs2", r => SimilarSongsAsync(r, "similarSongs2"));
        On("getSimilarSongs", r => SimilarSongsAsync(r, "similarSongs"));

        On("getTopSongs", async r =>
        {
            var key = NameNormalizer.Key(r.Require("artist"));
            var songs = await r.Db.Tracks.Where(t => t.Artist.NormalizedName == key)
                .OrderByDescending(t => r.Db.PlayEvents.Count(p => p.TrackId == t.Id)).ThenBy(t => t.Album.Year).ThenBy(t => t.TrackNumber)
                .Take(r.Int("count", 50, 1, MaxPage)).Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
            return SongList(r, "topSongs", songs);
        });
    }

    /// <summary>
    /// « Mix » des applis : la radio de SonNuage (même genre, puis même artiste, puis le reste), partie d'un titre,
    /// d'un album ou d'un artiste. Un titre de départ n'est pas repris dans son propre mix.
    /// </summary>
    private static async Task<object> SimilarSongsAsync(SubsonicRequest r, string element)
    {
        var raw = r.Require("id");
        List<Guid> seeds, exclude = [];
        if (SubsonicIds.Parse(raw, SubsonicIds.Artist) is { } artist)
            seeds = await r.Db.Tracks.Where(t => t.ArtistId == artist || t.Album.ArtistId == artist).Select(t => t.Id).Take(RadioRequest.MaxIds).ToListAsync(r.Ct);
        else if (SubsonicIds.Parse(raw, SubsonicIds.Album) is { } album)
            seeds = await r.Db.Tracks.Where(t => t.AlbumId == album).Select(t => t.Id).Take(RadioRequest.MaxIds).ToListAsync(r.Ct);
        else if (SubsonicIds.Parse(raw, SubsonicIds.Track) is { } track && await r.Db.Tracks.AnyAsync(t => t.Id == track, r.Ct))
            seeds = exclude = [track];
        else seeds = [];
        if (seeds.Count == 0) throw SubsonicException.NotFound("Item");

        var ids = await Api.DiscoveryEndpoints.NextAsync(r.Db, new RadioRequest(seeds, exclude, r.Int("count", 50, 1, MaxPage)), r.Ct);
        var rows = await r.Db.Tracks.Where(t => ids.Contains(t.Id)).Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
        return SongList(r, element, ids.Select(id => rows.First(s => s.Id == id)));
    }

    private static SubsonicNode SongList(SubsonicRequest r, string element, IEnumerable<SongRow> songs)
    {
        var root = r.Ok();
        var list = root.Add(element).List("song");
        foreach (var song in songs) list.Add().Fill(song);
        return root;
    }

    private static IQueryable<Track> FilterSongs(IQueryable<Track> query, string? genre, int? fromYear, int? toYear)
    {
        if (!string.IsNullOrEmpty(genre)) query = query.Where(t => t.Genre == genre);
        if (fromYear is { } from) query = query.Where(t => t.Year >= from);
        if (toYear is { } to) query = query.Where(t => t.Year <= to);
        return query;
    }

    private static async Task<object> AlbumListAsync(SubsonicRequest r, string element, bool legacy)
    {
        var type = r.Require("type");
        var (db, uid) = (r.Db, r.UserId);
        IQueryable<Album> albums = db.Albums;
        albums = type switch
        {
            "random" => albums.OrderBy(_ => EF.Functions.Random()),
            "newest" => albums.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id),
            "alphabeticalByName" => albums.OrderBy(a => a.Title).ThenBy(a => a.Id),
            "alphabeticalByArtist" => albums.OrderBy(a => a.Artist.SortName).ThenBy(a => a.Year).ThenBy(a => a.Id),
            "recent" => albums.Where(a => db.PlayEvents.Any(p => p.UserId == uid && p.Track.AlbumId == a.Id))
                .OrderByDescending(a => db.PlayEvents.Where(p => p.UserId == uid && p.Track.AlbumId == a.Id).Max(p => p.PlayedAt)).ThenBy(a => a.Id),
            "frequent" or "highest" => albums.Where(a => db.PlayEvents.Any(p => p.UserId == uid && p.Track.AlbumId == a.Id))
                .OrderByDescending(a => db.PlayEvents.Count(p => p.UserId == uid && p.Track.AlbumId == a.Id)).ThenBy(a => a.Id),
            "starred" => albums.Where(a => db.FavoriteAlbums.Any(f => f.UserId == uid && f.AlbumId == a.Id))
                .OrderByDescending(a => db.FavoriteAlbums.Where(f => f.UserId == uid && f.AlbumId == a.Id).Max(f => f.CreatedAt)).ThenBy(a => a.Id),
            "byYear" => ByYear(albums, r.Int("fromYear", 0), r.Int("toYear", 9999)),
            "byGenre" => GenreFilter(albums, r.Require("genre")).OrderBy(a => a.Title).ThenBy(a => a.Id),
            _ => throw new SubsonicException(SubsonicError.Generic, $"Unknown list type: {type}"),
        };
        var rows = await albums.Skip(r.Int("offset", 0)).Take(r.Int("size", 10, 1, MaxPage)).Select(Album(db, uid)).ToListAsync(r.Ct);
        var root = r.Ok();
        var list = root.Add(element).List("album");
        foreach (var album in rows) list.Add().Fill(album, legacy);
        return root;

        static IQueryable<Album> ByYear(IQueryable<Album> albums, int from, int to) => from <= to
            ? albums.Where(a => a.Year >= from && a.Year <= to).OrderBy(a => a.Year).ThenBy(a => a.Title).ThenBy(a => a.Id)
            : albums.Where(a => a.Year >= to && a.Year <= from).OrderByDescending(a => a.Year).ThenBy(a => a.Title).ThenBy(a => a.Id);

        static IQueryable<Album> GenreFilter(IQueryable<Album> albums, string genre) => albums.Where(a => a.Tracks.Any(t => t.Genre == genre));
    }

    /// <summary>
    /// Recherche par mots (sans casse ni accents). Une requête vide (ou <c>""</c>) liste toute la bibliothèque par pages :
    /// c'est ainsi que Symfonium et d'autres la synchronisent.
    /// </summary>
    private static async Task<object> SearchAsync(SubsonicRequest r, string element, bool legacy)
    {
        var text = (r.Get("query") ?? "").Replace("\"", "").Trim();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NameNormalizer.Key).Where(k => k.Length > 0).ToList();
        var (db, uid) = (r.Db, r.UserId);

        var artists = db.Artists.Where(a => a.Albums.Any());
        var albums = db.Albums.AsQueryable();
        var songs = db.Tracks.AsQueryable();
        foreach (var token in tokens)
        {
            artists = artists.Where(a => a.NormalizedName.Contains(token));
            albums = albums.Where(a => a.NormalizedTitle.Contains(token) || a.Artist.NormalizedName.Contains(token));
            songs = songs.Where(t => t.SearchKey.Contains(token));
        }

        var artistRows = await artists.OrderBy(a => a.SortName).ThenBy(a => a.Id)
            .Skip(r.Int("artistOffset", 0)).Take(r.Int("artistCount", 20, 0, MaxPage)).Select(ArtistRows(r.Db, r.UserId)).ToListAsync(r.Ct);
        var albumRows = await albums.OrderBy(a => a.Title).ThenBy(a => a.Id)
            .Skip(r.Int("albumOffset", 0)).Take(r.Int("albumCount", 20, 0, MaxPage)).Select(Album(db, uid)).ToListAsync(r.Ct);
        var songRows = await songs.OrderBy(t => t.Title).ThenBy(t => t.Id)
            .Skip(r.Int("songOffset", 0)).Take(r.Int("songCount", 20, 0, MaxPage)).Select(Song(db, uid)).ToListAsync(r.Ct);

        var root = r.Ok();
        var result = root.Add(element);
        var artistList = result.List("artist");
        foreach (var artist in artistRows) artistList.Add().Fill(artist);
        var albumList = result.List("album");
        foreach (var album in albumRows) albumList.Add().Fill(album, legacy);
        var songList = result.List("song");
        foreach (var song in songRows) songList.Add().Fill(song);
        return root;
    }

    private static async Task<object> StarredAsync(SubsonicRequest r, string element)
    {
        var (db, uid) = (r.Db, r.UserId);
        var artists = await db.FavoriteArtists.Where(f => f.UserId == uid).OrderByDescending(f => f.CreatedAt)
            .Select(f => f.Artist).Select(ArtistRows(db, uid)).ToListAsync(r.Ct);
        var albums = await db.FavoriteAlbums.Where(f => f.UserId == uid).OrderByDescending(f => f.CreatedAt)
            .Select(f => f.Album).Select(Album(db, uid)).ToListAsync(r.Ct);
        var songs = await db.Favorites.Where(f => f.UserId == uid).OrderByDescending(f => f.CreatedAt)
            .Select(f => f.Track).Select(Song(db, uid)).ToListAsync(r.Ct);
        var root = r.Ok();
        var node = root.Add(element);
        var artistList = node.List("artist");
        foreach (var artist in artists) artistList.Add().Fill(artist);
        var albumList = node.List("album");
        foreach (var album in albums) albumList.Add().Fill(album, legacy: element == "starred");
        var list = node.List("song");
        foreach (var song in songs) list.Add().Fill(song);
        return root;
    }
}
