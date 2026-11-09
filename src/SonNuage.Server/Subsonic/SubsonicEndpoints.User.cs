using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    private static void RegisterUser()
    {
        On("getUser", async r =>
        {
            var name = r.Get("username") ?? r.User.Username;
            var user = r.User;
            if (!string.Equals(name, user.Username, StringComparison.OrdinalIgnoreCase))
            {
                if (user.Role != Roles.Admin) throw new SubsonicException(SubsonicError.NotAuthorized, "Not authorized");
                var normalized = UserService.Normalize(name);
                user = await r.Db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, r.Ct) ?? throw SubsonicException.NotFound("User");
            }
            var root = r.Ok();
            root.Add("user").Attr("username", user.Username).Attr("scrobblingEnabled", true)
                .Attr("adminRole", user.Role == Roles.Admin).Attr("settingsRole", true).Attr("downloadRole", true).Attr("uploadRole", false)
                .Attr("playlistRole", true).Attr("coverArtRole", true).Attr("commentRole", false).Attr("podcastRole", false)
                .Attr("streamRole", true).Attr("jukeboxRole", false).Attr("shareRole", false).Attr("videoConversionRole", false)
                .Values("folder", [1]);
            return root;
        });

        On("getPlaylists", async r =>
        {
            var rows = await r.Db.Playlists.Where(p => p.OwnerId == r.UserId).OrderByDescending(p => p.UpdatedAt).Select(PlaylistRows).ToListAsync(r.Ct);
            var root = r.Ok();
            var list = root.Add("playlists").List("playlist");
            foreach (var row in rows) list.Add().Fill(row);
            return root;
        });

        On("getPlaylist", async r => await PlaylistAsync(r, r.RequireId("id", SubsonicIds.Playlist, "Playlist")));

        // Nouvelle playlist (name), ou contenu remplacé d'une existante (playlistId).
        On("createPlaylist", async r =>
        {
            var songIds = await ValidSongIdsAsync(r, r.GetAll("songId"));
            Playlist playlist;
            if (r.Get("playlistId") is { } existing)
            {
                playlist = await OwnedPlaylistAsync(r, SubsonicIds.Parse(existing, SubsonicIds.Playlist));
                await r.Db.PlaylistEntries.Where(e => e.PlaylistId == playlist.Id).ExecuteDeleteAsync(r.Ct);
                if (r.Get("name") is { Length: > 0 } rename) playlist.Name = rename.Trim();
            }
            else
            {
                playlist = new Playlist { OwnerId = r.UserId, Name = r.Require("name").Trim() };
                r.Db.Playlists.Add(playlist);
            }
            r.Db.PlaylistEntries.AddRange(songIds.Select((id, i) => new PlaylistEntry { PlaylistId = playlist.Id, TrackId = id, Position = i }));
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await r.Db.SaveChangesAsync(r.Ct);
            return await PlaylistAsync(r, playlist.Id);
        });

        On("updatePlaylist", async r =>
        {
            var playlist = await OwnedPlaylistAsync(r, SubsonicIds.Parse(r.Require("playlistId"), SubsonicIds.Playlist));
            if (r.Get("name") is { Length: > 0 } name) playlist.Name = name.Trim();
            if (r.Get("comment") is { } comment) playlist.Description = comment.Trim();

            var entries = await r.Db.PlaylistEntries.Where(e => e.PlaylistId == playlist.Id).OrderBy(e => e.Position).ToListAsync(r.Ct);
            var remove = r.GetAll("songIndexToRemove").Select(i => int.TryParse(i, out var n) ? n : -1)
                .Where(i => i >= 0 && i < entries.Count).Distinct().OrderDescending().ToList();
            foreach (var index in remove)
            {
                r.Db.PlaylistEntries.Remove(entries[index]);
                entries.RemoveAt(index);
            }
            var added = (await ValidSongIdsAsync(r, r.GetAll("songIdToAdd"))).Select(id => new PlaylistEntry { PlaylistId = playlist.Id, TrackId = id }).ToList();
            if (entries.Count + added.Count > PlaylistEndpoints.MaxEntries) throw new SubsonicException(SubsonicError.Generic, "Playlist is full");
            entries.AddRange(added);
            r.Db.PlaylistEntries.AddRange(added);
            for (var i = 0; i < entries.Count; i++) entries[i].Position = i;
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await r.Db.SaveChangesAsync(r.Ct);
            return r.Ok();
        });

        On("deletePlaylist", async r =>
        {
            var id = r.RequireId("id", SubsonicIds.Playlist, "Playlist");
            var deleted = await r.Db.Playlists.Where(p => p.Id == id && p.OwnerId == r.UserId).ExecuteDeleteAsync(r.Ct);
            return deleted > 0 ? r.Ok() : throw SubsonicException.NotFound("Playlist");
        });
    }

    private static async Task<SubsonicNode> PlaylistAsync(SubsonicRequest r, Guid id)
    {
        var row = await r.Db.Playlists.Where(p => p.Id == id && p.OwnerId == r.UserId).Select(PlaylistRows).FirstOrDefaultAsync(r.Ct)
            ?? throw SubsonicException.NotFound("Playlist");
        var songs = await r.Db.PlaylistEntries.Where(e => e.PlaylistId == id).OrderBy(e => e.Position).Select(e => e.Track)
            .Select(Song(r.Db, r.UserId)).ToListAsync(r.Ct);
        var root = r.Ok();
        var list = root.Add("playlist").Fill(row).List("entry");
        foreach (var song in songs) list.Add().Fill(song);
        return root;
    }

    private static async Task<Playlist> OwnedPlaylistAsync(SubsonicRequest r, Guid? id) =>
        id is { } value && await r.Db.Playlists.FirstOrDefaultAsync(p => p.Id == value && p.OwnerId == r.UserId, r.Ct) is { } playlist
            ? playlist
            : throw SubsonicException.NotFound("Playlist");

    /// <summary>Morceaux existants, dans l'ordre donné (les doublons sont permis dans une playlist).</summary>
    private static async Task<List<Guid>> ValidSongIdsAsync(SubsonicRequest r, IReadOnlyList<string> raw)
    {
        var ids = raw.Select(id => SubsonicIds.Parse(id, SubsonicIds.Track)).OfType<Guid>().ToList();
        var known = (await r.Db.Tracks.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(r.Ct)).ToHashSet();
        return ids.Where(known.Contains).ToList();
    }

    /// <summary>Fonctions que SonNuage n'a pas : réponse vide mais valide, pour que les clients ne s'arrêtent pas.</summary>
    private static void RegisterEmpty()
    {
        void Empty(string method, string element, string? list = null) => On(method, r =>
        {
            var root = r.Ok();
            var node = root.Add(element);
            if (list is not null) node.List(list);
            return root;
        });

        Empty("getPodcasts", "podcasts", "channel");
        Empty("getNewestPodcasts", "newestPodcasts", "episode");
        Empty("getInternetRadioStations", "internetRadioStations", "internetRadioStation");
        Empty("getShares", "shares", "share");
        Empty("getBookmarks", "bookmarks", "bookmark");
        Empty("getAlbumInfo", "albumInfo");
        Empty("getAlbumInfo2", "albumInfo");
        Empty("getVideos", "videos", "video");
        Empty("getChatMessages", "chatMessages", "chatMessage");
    }
}
