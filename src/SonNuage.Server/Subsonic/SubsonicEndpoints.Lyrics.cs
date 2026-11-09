using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Lyrics;

namespace SonNuage.Server.Subsonic;

public static partial class SubsonicEndpoints
{
    /// <summary>Paroles : le même service que le lecteur SonNuage (fichier .lrc d'abord, sinon LRCLIB).</summary>
    private static void RegisterLyrics()
    {
        // Extension OpenSubsonic songLyrics : paroles structurées, datées en millisecondes quand elles le sont.
        On("getLyricsBySongId", async r =>
        {
            var id = r.RequireId("id", SubsonicIds.Track, "Song");
            var track = await r.Db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, r.Ct) ?? throw SubsonicException.NotFound("Song");
            var root = r.Ok();
            var list = root.Add("lyricsList").List("structuredLyrics");
            if (await LyricsOfAsync(r, track) is not { } lyrics) return root;

            var node = list.Add().Attr("displayArtist", track.ArtistName).Attr("displayTitle", track.Title)
                .Attr("lang", "und").Attr("offset", 0).Attr("synced", lyrics.Synced);
            var lines = node.List("line");
            foreach (var line in lyrics.Lines)
            {
                var entry = lines.Add();
                if (lyrics.Synced && line.StartSeconds is { } start) entry.Attr("start", (long)Math.Round(start * 1000));
                entry.Text(line.Text);
            }
            return root;
        });

        // Ancienne méthode : texte brut, retrouvé par artiste et titre (sans casse ni accents).
        On("getLyrics", async r =>
        {
            var root = r.Ok();
            var node = root.Add("lyrics");
            var (artist, title) = (r.Get("artist"), r.Get("title"));
            if (string.IsNullOrWhiteSpace(title)) return root;

            var titleKey = NameNormalizer.Key(title);
            var candidates = await r.Db.Tracks.AsNoTracking().Where(t => t.SearchKey.Contains(titleKey)).Take(200).ToListAsync(r.Ct);
            var artistKey = string.IsNullOrWhiteSpace(artist) ? null : NameNormalizer.Key(artist);
            var track = candidates.FirstOrDefault(t => NameNormalizer.Key(t.Title) == titleKey && (artistKey is null || NameNormalizer.Key(t.ArtistName) == artistKey));
            if (track is null || await LyricsOfAsync(r, track) is not { } lyrics) return root;

            node.Attr("artist", track.ArtistName).Attr("title", track.Title).Text(string.Join('\n', lyrics.Lines.Select(l => l.Text)));
            return root;
        });
    }

    private static Task<LyricsDto?> LyricsOfAsync(SubsonicRequest r, Track track) =>
        r.Service<LyricsService>().FindAsync(track.Id, track.FilePath,
            new LyricsQuery(track.Title, track.ArtistName, track.AlbumTitle, track.DurationSeconds), r.Ct);
}
