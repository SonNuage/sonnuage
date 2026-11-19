using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Jam;

/// <summary>Issue d'une résolution : où on en est, et la piste si elle est jouable.</summary>
public sealed record JamResolution(JamResolve State, TrackDto? Track, string? VideoId = null);

/// <summary>
/// Retrouve le titre d'un Jam dans la bibliothèque, sinon lance (si autorisé) son import depuis YouTube et renvoie
/// l'avancement. La correspondance se fait sur le titre et l'artiste (sans casse ni accents), la durée départageant.
/// </summary>
public sealed class JamResolver(YtDlp ytDlp, YoutubeImporter importer, Microsoft.Extensions.Options.IOptions<SonNuageOptions> options, ILogger<JamResolver> log)
{
    /// <summary>Durée tolérée entre le titre du Jam et une piste de la bibliothèque.</summary>
    private const double DurationToleranceSeconds = 12;

    /// <summary>
    /// Pour un Jam on importe plus volontiers qu'une demande normale : l'utilisateur veut suivre un titre précis et
    /// connu, et un bon candidat (surtout une chaîne « - Topic » à la bonne durée) vaut mieux que rien. On exige quand
    /// même un score correct pour éviter un faux positif (une durée exacte ne suffit pas).
    /// </summary>
    private const int JamImportMinScore = 45;

    /// <summary>Durée YouTube acceptée comme « la même » que celle de l'hôte.</summary>
    private const double ImportDurationToleranceSeconds = 6;

    /// <summary>Cherche dans la bibliothèque. Trouvé : la piste ; sinon null.</summary>
    public async Task<TrackDto?> FindInLibraryAsync(SonNuageDbContext db, Guid userId, JamTrackDto track, CancellationToken ct)
    {
        var titleKey = NameNormalizer.Key(track.Title);
        var artistKey = NameNormalizer.Key(track.Artist);
        if (titleKey.Length == 0) return null;
        // SearchKey commence par la clé du titre (voir LibraryIndexer) : un titre identique se filtre en base, au lieu de
        // trier en mémoire 200 pistes prises au hasard parmi celles qui contiennent le mot (« Home », « Intro »…).
        var prefix = titleKey + " ";
        var candidates = await db.Tracks.AsNoTracking().Where(t => t.SearchKey.StartsWith(prefix)).Take(500).ToListAsync(ct);
        var match = candidates
            .Where(t => NameNormalizer.Key(t.Title) == titleKey && ArtistMatches(t.ArtistName, track.Artist))
            .OrderBy(t => track.DurationSeconds > 0 ? Math.Abs(t.DurationSeconds - track.DurationSeconds) : 0)
            .FirstOrDefault();
        if (match is null) return null;
        var dto = await LibraryEndpoints.TracksInOrderAsync(db, userId, [match.Id], ct);
        return dto.FirstOrDefault();
    }

    /// <summary>État d'un import déjà lancé pour cette vidéo : terminé (piste), en cours, ou échoué.</summary>
    public async Task<JamResolution> CheckImportAsync(SonNuageDbContext db, Guid userId, string videoId, CancellationToken ct)
    {
        var item = await db.YoutubeItems.AsNoTracking().FirstOrDefaultAsync(i => i.VideoId == videoId, ct);
        if (item is null) return new JamResolution(JamResolve.Unavailable, null);
        if (item.Status == YoutubeItemStatus.Failed || item.Status == YoutubeItemStatus.Skipped) return new JamResolution(JamResolve.Unavailable, null);
        if (item.Status == YoutubeItemStatus.Done && item.TrackId is { } id)
        {
            var dto = await LibraryEndpoints.TracksInOrderAsync(db, userId, [id], ct);
            return dto.Count > 0 ? new JamResolution(JamResolve.Matched, dto[0], videoId) : new JamResolution(JamResolve.Importing, null, videoId);
        }
        return new JamResolution(JamResolve.Importing, null, videoId);
    }

    /// <summary>
    /// Lance l'import YouTube du titre (meilleur résultat sûr). Renvoie l'identifiant de la vidéo mise en file, ou null
    /// si aucun résultat convaincant. Le suivi d'avancement se fait ensuite via <see cref="CheckImportAsync"/>.
    /// </summary>
    public async Task<string?> StartImportAsync(SonNuageDbContext db, JamTrackDto track, CancellationToken ct)
    {
        try
        {
            var target = track.DurationSeconds > 0 ? track.DurationSeconds : (double?)null;
            var query = new MatchQuery(track.Artist, track.Title, target);
            var ranked = await ytDlp.MatchAsync(query, options.Value.MatchOfficialMinScore, ct);
            if (PickImport(ranked, target) is not { } best) return null;
            var e = best.Entry;
            var video = new YoutubeVideoDto(e.Id, e.Title, e.Channel, e.DurationSeconds, e.Thumbnail, null, e.Album);
            await importer.EnqueueAsync(db, [video], albumHint: null, subscriptionId: null, ct);
            return e.Id;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Import YouTube du titre de Jam « {Title} » impossible", track.Title);
            return null;
        }
    }

    /// <summary>
    /// Le candidat à importer : d'abord un résultat à la durée de l'hôte, puis le mieux classé, mais toujours avec un
    /// score suffisant (une durée exacte ne rattrape pas un mauvais titre). Null si aucun ne convient.
    /// </summary>
    public static RankedEntry? PickImport(IReadOnlyList<RankedEntry> ranked, double? targetSeconds)
    {
        var acceptable = ranked.Where(r => r.Score >= JamImportMinScore).ToList();
        return (targetSeconds is { } d
                ? acceptable.FirstOrDefault(r => r.Entry.DurationSeconds is { } rd && Math.Abs(rd - d) <= ImportDurationToleranceSeconds)
                : null)
            ?? acceptable.FirstOrDefault();
    }

    private static bool ArtistMatches(string libraryArtist, string jamArtist)
    {
        var libKey = NameNormalizer.Key(libraryArtist);
        // Le Jam donne parfois plusieurs artistes séparés par des virgules ; il suffit qu'un corresponde.
        foreach (var part in jamArtist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (NameNormalizer.Key(part) == libKey || libKey.Contains(NameNormalizer.Key(part)) || NameNormalizer.Key(part).Contains(libKey))
                return true;
        return NameNormalizer.Key(jamArtist) == libKey;
    }
}
