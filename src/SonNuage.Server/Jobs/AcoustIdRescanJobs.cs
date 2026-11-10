using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Youtube;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Rescan AcoustID de la bibliothèque (toute, ou un artiste) : prépare, dans sa propre suite, une paire de tâches
/// par morceau - empreinte AcoustID (file « acoustid ») puis comparaison avec MusicBrainz (<see cref="LibraryMatchJob"/>).
/// Les morceaux déjà identifiés (identifiant d'enregistrement MusicBrainz connu) sont sautés, sauf <c>includeIdentified</c>.
/// Paramètres : <c>artistId</c> (facultatif), <c>apply</c> (appliquer les correspondances sûres),
/// <c>threshold</c> (seuil d'acceptation propre à la tâche, sinon celui des réglages).
/// </summary>
public sealed class AcoustIdRescanJob(YoutubeSettingsStore settings) : IJobHandler
{
    private const int Chunk = 500;

    public JobKind Kind => JobKinds.LibraryAcoustIdScan;

    public static JobPlan Plan(string title, Guid? artistId, bool apply, double? threshold, Guid? createdById, bool includeIdentified = false) =>
        new JobPlan(JobSuiteKinds.AcoustIdRescan, title, createdById)
            .Then(JobKinds.LibraryAcoustIdScan, Tx.Key("jobs.steps.plan"), new { artistId, apply, threshold, includeIdentified });

    /// <summary>Morceaux à vérifier : ceux de l'artiste (morceaux et albums), sans ceux déjà identifiés sauf demande.</summary>
    public static IQueryable<Track> Targets(IQueryable<Track> tracks, Guid? artistId, bool includeIdentified)
    {
        if (artistId is { } a) tracks = tracks.Where(t => t.ArtistId == a || t.Album.ArtistId == a);
        if (!includeIdentified) tracks = tracks.Where(t => t.MusicBrainzRecordingId == null);
        return tracks;
    }

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        if (string.IsNullOrWhiteSpace(settings.Settings.AcoustIdKey)) throw new InvalidOperationException(Tx.Key("errors.jobs.noAcoustIdKey"));
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var artistId = ctx.Get<Guid?>("artistId");
        var apply = ctx.Get<bool>("apply");
        var threshold = ctx.Get<double?>("threshold");

        var tracks = await Targets(db.Tracks.AsNoTracking(), artistId, ctx.Get<bool>("includeIdentified"))
            .OrderBy(t => t.AlbumArtistName).ThenBy(t => t.AlbumTitle).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .Select(t => new { t.Id, t.FilePath, t.Title, t.ArtistName })
            .ToListAsync(ct);
        ctx.Info(Tx.Key("jobs.rescan.planned", ("count", tracks.Count)));

        for (var i = 0; i < tracks.Count; i += Chunk)
        {
            var plan = new JobPlan(JobSuiteKinds.AcoustIdRescan, "");
            foreach (var t in tracks.Skip(i).Take(Chunk))
            {
                var title = $"{t.ArtistName} - {t.Title}";
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
/// sinon le réglage YouTube).
/// Album, édition MusicBrainz, numéros et année seulement si l'édition trouvée est le même album. Le genre seulement s'il manque.
/// Les morceaux modifiés à la main (verrouillés) ne sont jamais touchés. Sans « appliquer » ou sous le seuil, la
/// correction est gardée pour la revue groupée de l'admin (voir <see cref="RescanReview"/>) ; un résultat déjà refusé
/// n'est ni proposé ni appliqué.
/// </summary>
public sealed class LibraryMatchJob(MetadataResolver resolver, YoutubeSettingsStore settings, RescanReview review) : IJobHandler
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

        var threshold = ctx.Get<double?>("threshold") ?? settings.Settings.AutoAcceptThreshold;
        var outcome = await review.ConcludeAsync(db, track, best, apply, threshold, ctx.SuiteId, ct);
        if (outcome.Changes.Count == 0)
        {
            ctx.Info(Tx.Key("jobs.rescan.unchanged"));
            ctx.Set("result", outcome.Result);
            return;
        }
        foreach (var (field, from, to, _) in outcome.Changes)
            ctx.Info(Tx.Key("jobs.rescan.change", ("field", Tx.Key("jobs.fields." + field)), ("from", Tx.Literal(from ?? "∅")), ("to", Tx.Literal(to))));
        ctx.Set("result", outcome.Result);
        ctx.Info(Tx.Key("jobs.rescan.result." + outcome.Result, ("score", Math.Round(best.Score * 100)), ("threshold", Math.Round(threshold * 100))));
    }

    internal sealed record Change(string Field, string? From, string To, Action<Track> Apply);

    /// <summary>
    /// Ce que la correspondance changerait, et si elle est appliquée (« applied ») ou non (« same », « rejected » : déjà
    /// refusée par l'admin, « proposed », « locked », « low »).
    /// </summary>
    internal sealed record MatchOutcome(string Result, IReadOnlyList<Change> Changes)
    {
        public void Apply(Track track)
        {
            if (Result != "applied") return;
            foreach (var change in Changes) change.Apply(track);
        }
    }

    internal static MatchOutcome Evaluate(Track track, MetadataCandidateDto best, bool apply, double threshold, bool rejected = false)
    {
        var changes = Changes(track, best);
        var result = changes.Count == 0 ? "same"
            : rejected ? "rejected"
            : !apply ? "proposed"
            : track.MetadataLocked ? "locked"
            : best.Score < threshold ? "low"
            : "applied";
        return new MatchOutcome(result, changes);
    }

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

        void Id(string field, Guid? current, string? value, Action<Track, Guid> set)
        {
            if (Library.MusicBrainzId.Parse(value) is { } id && current != id) list.Add(new Change(field, current?.ToString(), id.ToString(), x => set(x, id)));
        }

        Text("title", t.Title, c.Title, (x, v) => x.Title = v);
        Text("artist", t.ArtistName, c.Artist, (x, v) => x.ArtistName = v);
        Id("recordingId", t.MusicBrainzRecordingId, c.RecordingId, (x, v) => x.MusicBrainzRecordingId = v);
        // L'édition retenue est le même album (orthographe, numéros) : sinon on ne déplace pas le morceau d'album.
        if (c.Album is not null && TitleParser.Similarity(c.Album, t.AlbumTitle) >= SameAlbum)
        {
            Id("releaseId", t.MusicBrainzReleaseId, c.ReleaseId, (x, v) => x.MusicBrainzReleaseId = v);
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
