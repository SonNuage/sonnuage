using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Search;
using SonNuage.Server.Youtube;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Rescan AcoustID de la bibliothèque (toute, ou un artiste) : prépare, dans sa propre suite, une paire de tâches
/// par morceau — empreinte AcoustID (file « acoustid ») puis comparaison avec MusicBrainz (<see cref="LibraryMatchJob"/>).
/// Paramètres : <c>artistId</c> (facultatif), <c>apply</c> (appliquer les correspondances sûres),
/// <c>threshold</c> (seuil d'acceptation propre à la tâche, sinon celui des réglages).
/// </summary>
public sealed class AcoustIdRescanJob(YoutubeSettingsStore settings) : IJobHandler
{
    private const int Chunk = 500;

    public JobKind Kind => JobKinds.LibraryAcoustIdScan;

    public static JobPlan Plan(string title, Guid? artistId, bool apply, double? threshold, Guid? createdById) =>
        new JobPlan(JobSuiteKinds.AcoustIdRescan, title, createdById)
            .Then(JobKinds.LibraryAcoustIdScan, Tx.Key("jobs.steps.plan"), new { artistId, apply, threshold });

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        if (string.IsNullOrWhiteSpace(settings.Settings.AcoustIdKey)) throw new InvalidOperationException(Tx.Key("errors.jobs.noAcoustIdKey"));
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var artistId = ctx.Get<Guid?>("artistId");
        var apply = ctx.Get<bool>("apply");
        var threshold = ctx.Get<double?>("threshold");

        var query = db.Tracks.AsNoTracking();
        if (artistId is { } a) query = query.Where(t => t.ArtistId == a || t.Album.ArtistId == a);
        var tracks = await query
            .OrderBy(t => t.AlbumArtistName).ThenBy(t => t.AlbumTitle).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .Select(t => new { t.Id, t.FilePath, t.Title, t.ArtistName })
            .ToListAsync(ct);
        ctx.Info(Tx.Key("jobs.rescan.planned", ("count", tracks.Count)));

        for (var i = 0; i < tracks.Count; i += Chunk)
        {
            var plan = new JobPlan(JobSuiteKinds.AcoustIdRescan, "");
            foreach (var t in tracks.Skip(i).Take(Chunk))
            {
                var title = $"{t.ArtistName} – {t.Title}";
                plan.Add(JobKinds.AcoustIdIdentify, title, new { path = t.FilePath }, subjectId: t.Id);
                plan.Then(JobKinds.LibraryMatch, title, new { trackId = t.Id, apply, threshold }, subjectId: t.Id);
            }
            await ctx.AppendAsync(db, plan);
            ctx.Report((double)Math.Min(tracks.Count, i + Chunk) / Math.Max(1, tracks.Count));
        }
    }
}

/// <summary>
/// Compare un morceau de la bibliothèque aux enregistrements MusicBrainz reconnus par son empreinte.
/// Prudence : titre et artiste sont corrigés si la correspondance dépasse le seuil d'acceptation (celui de la tâche,
/// sinon le réglage YouTube) ;
/// album, numéros et année seulement si l'édition trouvée est le même album ; le genre seulement s'il manque.
/// Les morceaux modifiés à la main (verrouillés) ne sont jamais touchés. Sans « appliquer », tout est seulement journalisé.
/// </summary>
public sealed class LibraryMatchJob(MetadataResolver resolver, YoutubeSettingsStore settings, CoverLocator covers, SearchIndexer search) : IJobHandler
{
    private const double SameAlbum = 0.8;

    public JobKind Kind => JobKinds.LibraryMatch;

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var trackId = ctx.Get<Guid>("trackId");
        var apply = ctx.Get<bool>("apply");
        var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == trackId, ct) ?? throw new JobAbortedException(Tx.Key("jobs.rescan.trackGone"));

        var matches = ctx.Input["acoustid"]?.Deserialize<List<AcoustIdMatch>>(JobContext.Json) ?? [];
        if (matches.Count == 0)
        {
            ctx.Info(Tx.Key("jobs.rescan.noMatch"));
            ctx.Set("result", "none");
            return;
        }
        var candidates = await resolver.FromAcoustIdAsync(matches, track.Title, track.ArtistName, track.AlbumTitle, track.DurationSeconds, ct);
        if (candidates.Count == 0)
        {
            ctx.Info(Tx.Key("jobs.rescan.noMatch"));
            ctx.Set("result", "none");
            return;
        }

        var best = candidates[0];
        ctx.Set("best", best);
        ctx.Info(Tx.Key("jobs.rescan.best", ("artist", best.Artist), ("title", best.Title), ("album", best.Album ?? "?"), ("score", Math.Round(best.Score * 100))));

        var changes = Changes(track, best);
        if (changes.Count == 0)
        {
            ctx.Info(Tx.Key("jobs.rescan.unchanged"));
            ctx.Set("result", "same");
            return;
        }
        foreach (var (field, from, to, _) in changes)
            ctx.Info(Tx.Key("jobs.rescan.change", ("field", Tx.Key("jobs.fields." + field)), ("from", Tx.Literal(from ?? "∅")), ("to", Tx.Literal(to))));

        var threshold = ctx.Get<double?>("threshold") ?? settings.Settings.AutoAcceptThreshold;
        string result;
        if (!apply) result = "proposed";
        else if (track.MetadataLocked) result = "locked";
        else if (best.Score < threshold) result = "low";
        else
        {
            foreach (var change in changes) change.Apply(track);
            await AdminEndpoints.ReassignAsync(db, covers, [track], ct);
            search.RequestReindex();
            result = "applied";
        }
        ctx.Set("result", result);
        ctx.Info(Tx.Key("jobs.rescan.result." + result, ("score", Math.Round(best.Score * 100)), ("threshold", Math.Round(threshold * 100))));
    }

    private sealed record Change(string Field, string? From, string To, Action<Track> Apply);

    private static List<Change> Changes(Track t, MetadataCandidateDto c)
    {
        var list = new List<Change>();
        void Text(string field, string? current, string? value, Action<Track, string> set)
        {
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(current?.Trim(), value.Trim(), StringComparison.Ordinal))
                list.Add(new Change(field, current, value.Trim(), x => set(x, value.Trim())));
        }
        void Number(string field, int? current, int? value, Action<Track, int> set)
        {
            if (value is { } v && current != v) list.Add(new Change(field, current?.ToString(), v.ToString(), x => set(x, v)));
        }

        Text("title", t.Title, c.Title, (x, v) => x.Title = v);
        Text("artist", t.ArtistName, c.Artist, (x, v) => x.ArtistName = v);
        // L'édition retenue est le même album (orthographe, numéros) : sinon on ne déplace pas le morceau d'album.
        if (c.Album is not null && TitleParser.Similarity(c.Album, t.AlbumTitle) >= SameAlbum)
        {
            Text("album", t.AlbumTitle, c.Album, (x, v) => x.AlbumTitle = v);
            Text("albumArtist", t.AlbumArtistName, c.AlbumArtist, (x, v) => x.AlbumArtistName = v);
            Number("trackNumber", t.TrackNumber, c.TrackNumber, (x, v) => x.TrackNumber = v);
            Number("discNumber", t.DiscNumber, c.DiscNumber, (x, v) => x.DiscNumber = v);
            Number("year", t.Year, c.Year, (x, v) => x.Year = v);
        }
        if (string.IsNullOrWhiteSpace(t.Genre)) Text("genre", t.Genre, c.Genre, (x, v) => x.Genre = v);
        return list;
    }
}
