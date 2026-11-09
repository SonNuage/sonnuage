using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Radio;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

/// <summary>
/// Radios thématiques dans les applis Subsonic : des playlists en lecture seule (« Radio Pop bonne humeur »…), d'un
/// propriétaire « SonNuage », dont le contenu est tiré à chaque ouverture.
/// </summary>
public static partial class SubsonicEndpoints
{
    private const int StationPlaylistSize = 50;
    private const string StationOwner = "SonNuage";

    /// <summary>Identifiant de playlist fixe d'une station (dérivé de son nom).</summary>
    internal static Guid StationPlaylistId(string stationId) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("sonnuage-radio:" + stationId)));

    private static readonly Dictionary<Guid, string> StationsByPlaylist = StationCatalog.Seasons.Concat(StationCatalog.Moods)
        .Select(s => s.Id).Append(StationCatalog.WeatherId).ToDictionary(StationPlaylistId);

    internal static string? StationOfPlaylist(Guid id) => StationsByPlaylist.GetValueOrDefault(id);

    private static async Task<List<PlaylistRow>> StationPlaylistsAsync(SubsonicRequest r)
    {
        var language = Translations.Instance.Resolve(r.User.Language);
        var stations = await r.Service<RadioStations>().ListAsync(r.Db, r.UserId, language, r.Ct);
        // Changée chaque jour : les applis qui gardent les playlists en cache les relisent.
        var today = new DateTimeOffset(r.Service<TimeProvider>().GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        return stations.Select(s => new PlaylistRow(StationPlaylistId(s.Id),
                Translations.Instance.Get(language, "radio.playing", ("name", s.Name)), s.Description, StationOwner,
                StationPlaylistSize, StationPlaylistSize * 210, today, today, s.CoverAlbumIds.Count > 0 ? s.CoverAlbumIds[0] : null))
            .ToList();
    }

    private static async Task<SubsonicNode> StationPlaylistAsync(SubsonicRequest r, Guid playlistId, string stationId)
    {
        var row = (await StationPlaylistsAsync(r)).FirstOrDefault(p => p.Id == playlistId) ?? throw SubsonicException.NotFound("Playlist");
        var ids = await r.Service<RadioStations>().NextAsync(r.Db, r.UserId, stationId, new StationNextRequest([], StationPlaylistSize), r.Ct)
            ?? throw SubsonicException.NotFound("Playlist");
        var songs = await r.Db.Tracks.Where(t => ids.Contains(t.Id)).Select(Song(r.Db, r.UserId)).ToDictionaryAsync(s => s.Id, r.Ct);
        var ordered = ids.Where(songs.ContainsKey).Select(id => songs[id]).ToList();
        var root = r.Ok();
        var list = root.Add("playlist").Fill(row with
        {
            SongCount = ordered.Count, Duration = ordered.Sum(s => s.Duration), Changed = r.Service<TimeProvider>().GetUtcNow(),
        }).List("entry");
        foreach (var song in ordered) list.Add().Fill(song);
        return root;
    }
}
