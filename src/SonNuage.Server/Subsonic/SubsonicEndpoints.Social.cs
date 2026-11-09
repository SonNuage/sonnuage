using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SonNuage.Server.Discovery;
using static SonNuage.Server.Subsonic.SubsonicLibrary;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    /// <summary>Artistes proches gardés en mémoire un jour (dix minutes si ListenBrainz n'a pas répondu).</summary>
    private static readonly TimeSpan SimilarFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan SimilarFailedFor = TimeSpan.FromMinutes(10);

    private static void RegisterSocial()
    {
        On("getArtistInfo2", r => ArtistInfoAsync(r, "artistInfo2"));
        On("getArtistInfo", r => ArtistInfoAsync(r, "artistInfo"));

        // Ce qu'écoutent les comptes du serveur en ce moment, dans leurs applis Subsonic.
        On("getNowPlaying", async r =>
        {
            var entries = r.Service<NowPlayingBoard>().Current();
            var trackIds = entries.Select(e => e.TrackId).Distinct().ToList();
            var songs = await r.Db.Tracks.Where(t => trackIds.Contains(t.Id)).Select(Song(r.Db, r.UserId)).ToDictionaryAsync(s => s.Id, r.Ct);
            var now = r.Service<TimeProvider>().GetUtcNow();
            var root = r.Ok();
            var list = root.Add("nowPlaying").List("entry");
            var player = 0;
            foreach (var entry in entries)
            {
                player++;
                if (!songs.TryGetValue(entry.TrackId, out var song)) continue;
                list.Add().Fill(song).Attr("username", entry.Username).Attr("minutesAgo", (int)(now - entry.StartedAt).TotalMinutes)
                    .Attr("playerId", player).Attr("playerName", entry.Client);
            }
            return root;
        });
    }

    /// <summary>
    /// Fiche d'artiste : son identifiant MusicBrainz et les artistes proches selon ListenBrainz qui sont dans la
    /// bibliothèque (rien n'est envoyé d'autre que l'identifiant MusicBrainz de l'artiste).
    /// </summary>
    private static async Task<object> ArtistInfoAsync(SubsonicRequest r, string element)
    {
        var id = r.RequireId("id", SubsonicIds.Artist, "Artist");
        var mbid = await r.Db.Artists.Where(a => a.Id == id).Select(a => new { a.MusicBrainzId }).FirstOrDefaultAsync(r.Ct)
            ?? throw SubsonicException.NotFound("Artist");
        var root = r.Ok();
        var node = root.Add(element).Element("musicBrainzId", mbid.MusicBrainzId);
        var list = node.List("similarArtist");
        if (mbid.MusicBrainzId is not { } artistMbid) return root;

        var similar = await SimilarArtistsAsync(r, artistMbid);
        var ranked = similar.Select((s, i) => (s.MusicBrainzId, Rank: i)).ToDictionary(x => x.MusicBrainzId, x => x.Rank);
        var keys = ranked.Keys.ToList();
        var rows = await r.Db.Artists.Where(a => a.Id != id && a.MusicBrainzId != null && keys.Contains(a.MusicBrainzId) && a.Albums.Any())
            .Select(ArtistRows(r.Db, r.UserId)).ToListAsync(r.Ct);
        foreach (var row in rows.OrderBy(a => ranked[a.MusicBrainzId!]).Take(r.Int("count", 20, 1, 100)))
            list.Add().Fill(row);
        return root;
    }

    private static async Task<IReadOnlyList<SimilarArtist>> SimilarArtistsAsync(SubsonicRequest r, string artistMbid)
    {
        var cache = r.Service<IMemoryCache>();
        var key = "subsonic-similar:" + artistMbid;
        if (cache.TryGetValue(key, out IReadOnlyList<SimilarArtist>? known) && known is not null) return known;
        try
        {
            var found = await r.Service<ISimilarArtistSource>().SimilarArtistsAsync(artistMbid, r.Ct);
            return cache.Set(key, found, SimilarFor);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !r.Ct.IsCancellationRequested)
        {
            return cache.Set<IReadOnlyList<SimilarArtist>>(key, [], SimilarFailedFor);
        }
    }
}
