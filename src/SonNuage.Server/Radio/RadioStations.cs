using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Data;
using SonNuage.Server.Search;

namespace SonNuage.Server.Radio;

/// <summary>
/// Radios thématiques : quelles stations proposer aujourd'hui, et leurs morceaux. Chaque morceau de la bibliothèque
/// reçoit une note par station (<see cref="StationCatalog"/>) ; les notes du compte s'y ajoutent (favori ou noté 4–5 :
/// un peu plus souvent ; noté 1–2 : jamais). Une station n'est proposée qu'avec assez de morceaux.
/// </summary>
public sealed class RadioStations(IMemoryCache cache, IWeatherSource weather, TimeProvider time)
{
    public const int MaxCount = 100;
    public const int MaxExclude = 1000;

    /// <summary>Morceaux qu'il faut au moins pour proposer une station.</summary>
    public int MinTracks { get; init; } = 15;
    /// <summary>Plus court, ce n'est pas un morceau à passer à la radio (intro, interlude).</summary>
    public double MinDurationSeconds { get; init; } = 60;
    private const string ProfilesKey = "radio-profiles";
    private const string GenresKey = "radio-genres";
    /// <summary>Radio d'une ambiance demandée dans la recherche : « ambiance: » suivi de la demande (<see cref="AmbianceQuery.Key"/>).</summary>
    public const string AmbiancePrefix = "ambiance:";
    private static readonly TimeSpan ProfilesFor = TimeSpan.FromMinutes(10);

    /// <summary>Stations du jour : saisons en cours, ambiances, météo (si un lieu est réglé).</summary>
    public async Task<IReadOnlyList<StationDefinition>> TodayAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var list = StationCatalog.Seasons.Where(s => s.Window!.Contains(today)).Concat(StationCatalog.Moods).ToList();
        if (await weather.NowAsync(ct) is { } now) list.Add(StationCatalog.ForWeather(now));
        return list;
    }

    public async Task<List<RadioStationDto>> ListAsync(SonNuageDbContext db, Guid userId, string language, CancellationToken ct)
    {
        var profiles = await ProfilesAsync(db, ct);
        var personal = await PersonalAsync(db, userId, ct);
        var now = await weather.NowAsync(ct);
        var t = Translations.Instance;
        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).DayNumber;
        var result = new List<RadioStationDto>();
        foreach (var station in await TodayAsync(ct))
        {
            var candidates = Candidates(station, profiles, personal, MinDurationSeconds);
            if (candidates.Count < MinTracks) continue;
            // Pochettes : parmi les mieux notés, une sélection qui change chaque jour.
            var rng = new Random(HashCode.Combine(day, StableHash(station.Id)));
            var covers = candidates.OrderByDescending(c => c.Score).Take(40).Where(c => c.Profile.AlbumHasCover)
                .OrderBy(_ => rng.Next()).Select(c => c.Profile.AlbumId).Distinct().Take(4).ToList();
            var description = station.Kind == RadioStationKinds.Weather && now is not null
                ? t.Get(language, $"radio.weather.{StationCatalog.Category(now).ToString().ToLowerInvariant()}",
                    ("temp", (int)Math.Round(now.TemperatureC)), ("place", now.Place))
                : t.Get(language, $"radio.stations.{station.Id}.description");
            result.Add(new RadioStationDto(station.Id, station.Kind, t.Get(language, $"radio.stations.{station.Id}.name"), description, covers, station.Color));
        }
        return result;
    }

    /// <summary>Suite de la station ; null si elle n'est pas proposée aujourd'hui.</summary>
    public async Task<List<Guid>?> NextAsync(SonNuageDbContext db, Guid userId, string stationId, StationNextRequest req, CancellationToken ct)
    {
        var ambiance = stationId.StartsWith(AmbiancePrefix, StringComparison.Ordinal)
            ? await ParseAmbianceAsync(db, stationId[AmbiancePrefix.Length..], ct)
            : null;
        var station = ambiance is not null ? AmbianceStation(ambiance) : (await TodayAsync(ct)).FirstOrDefault(s => s.Id == stationId);
        if (station is null) return null;
        var candidates = Candidates(station, await ProfilesAsync(db, ct), await PersonalAsync(db, userId, ct), MinDurationSeconds);
        // Une ambiance demandée joue même avec peu de morceaux : la radio recommence quand tout est passé.
        if (ambiance is null && candidates.Count < MinTracks) return null;
        if (candidates.Count == 0) return [];
        var exclude = (req.ExcludeTrackIds ?? []).Take(MaxExclude).ToHashSet();
        return Pick(candidates, exclude, Math.Clamp(req.Count, 1, MaxCount), Random.Shared);
    }

    /// <summary>Lit une demande d'ambiance avec les genres de la bibliothèque ; null si ce n'en est pas une.</summary>
    public async Task<AmbianceQuery?> ParseAmbianceAsync(SonNuageDbContext db, string query, CancellationToken ct) =>
        AmbianceParser.Parse(query, await GenresAsync(db, ct));

    /// <summary>Les <paramref name="count"/> morceaux qui répondent le mieux à l'ambiance (titres mal notés exclus).</summary>
    public async Task<List<Guid>> AmbianceAsync(SonNuageDbContext db, Guid userId, AmbianceQuery query, int count, CancellationToken ct)
    {
        var candidates = Candidates(AmbianceStation(query), await ProfilesAsync(db, ct), await PersonalAsync(db, userId, ct), MinDurationSeconds);
        return Rank(candidates, count, StableHash(query.Key));
    }

    public static StationDefinition AmbianceStation(AmbianceQuery query) =>
        new(AmbiancePrefix + query.Key, RadioStationKinds.Ambiance, "#7950f2", null, query.Score, AmbianceQuery.Threshold);

    /// <summary>
    /// Classement stable : meilleure note d'abord, à égalité un ordre propre à la demande ; au plus deux titres d'un même
    /// artiste tant que d'autres répondent.
    /// </summary>
    internal static List<Guid> Rank(IReadOnlyList<Candidate> candidates, int count, int seed)
    {
        var order = candidates.OrderByDescending(c => Math.Round(c.Score, 3)).ThenBy(c => HashCode.Combine(seed, c.Profile.Id)).ToList();
        var picked = new List<Guid>();
        var perArtist = new Dictionary<Guid, int>();
        foreach (var limit in new[] { 2, int.MaxValue })
        {
            foreach (var c in order)
            {
                if (picked.Count >= count) break;
                if (picked.Contains(c.Profile.Id) || perArtist.GetValueOrDefault(c.Profile.ArtistId) >= limit) continue;
                picked.Add(c.Profile.Id);
                perArtist[c.Profile.ArtistId] = perArtist.GetValueOrDefault(c.Profile.ArtistId) + 1;
            }
        }
        return picked;
    }

    internal sealed record Candidate(TrackProfile Profile, double Score);

    /// <summary>Notes du compte : morceaux préférés (favori, 4–5 étoiles) et écartés (1–2 étoiles, sur le titre, l'album ou l'artiste).</summary>
    internal sealed record Personal(IReadOnlySet<Guid> Liked, IReadOnlyDictionary<Guid, int> Ratings)
    {
        public static readonly Personal None = new(new HashSet<Guid>(), new Dictionary<Guid, int>());
    }

    /// <summary>Part des morceaux qu'une station sait juger (note au-dessus de zéro) qu'elle garde : les mieux notés.</summary>
    public const double BestShare = 0.12;
    /// <summary>Morceaux gardés au moins, quand autant passent le seuil : assez pour une radio variée.</summary>
    public const int MinKept = 60;

    /// <summary>
    /// Morceaux de la station : au-dessus de son seuil, une version par chanson, puis seulement les mieux notés
    /// (<see cref="BestShare"/>). Le seuil
    /// seul laissait passer près de la moitié des morceaux analysés dans les ambiances calmes.
    /// </summary>
    internal static List<Candidate> Candidates(StationDefinition station, IReadOnlyList<TrackProfile> profiles, Personal personal, double minDurationSeconds = 60)
    {
        var list = new List<Candidate>();
        var judged = 0;
        foreach (var p in profiles)
        {
            if (p.DurationSeconds < minDurationSeconds) continue;
            var rating = personal.Ratings.TryGetValue(p.Id, out var r) ? r
                : personal.Ratings.TryGetValue(p.AlbumId, out r) ? r
                : personal.Ratings.TryGetValue(p.ArtistId, out r) ? r : 0;
            if (rating is > 0 and <= 2) continue;
            var score = station.Score(p);
            if (score > 0) judged++;
            if (score < station.Threshold) continue;
            if (rating >= 4 || personal.Liked.Contains(p.Id)) score = Math.Min(1, score + 0.1);
            list.Add(new Candidate(p, score));
        }
        return Best(OneVersionPerSong(list), Math.Max(MinKept, (int)Math.Ceiling(BestShare * judged)));
    }

    /// <summary>D'une chanson d'un artiste (<see cref="TrackProfile.Song"/>), la version la mieux notée : pas huit remix du même titre.</summary>
    private static List<Candidate> OneVersionPerSong(List<Candidate> list) =>
        list.GroupBy(c => (c.Profile.ArtistId, Song: c.Profile.Song.Length > 0 ? c.Profile.Song : c.Profile.Id.ToString()))
            .Select(g => g.MaxBy(c => c.Score)!).ToList();

    /// <summary>Les <paramref name="keep"/> mieux notés ; ceux à égalité avec le dernier gardé restent aussi.</summary>
    private static List<Candidate> Best(List<Candidate> list, int keep)
    {
        if (list.Count <= keep) return list;
        var cut = list.Select(c => c.Score).OrderDescending().ElementAt(keep - 1);
        return list.Where(c => c.Score >= cut).ToList();
    }

    /// <summary>
    /// Tirage pondéré (les mieux notés sortent plus souvent), sans morceau exclu, au plus deux titres d'un même
    /// artiste et jamais deux de suite tant que c'est possible.
    /// </summary>
    internal static List<Guid> Pick(IReadOnlyList<Candidate> candidates, IReadOnlySet<Guid> exclude, int count, Random rng)
    {
        var pool = candidates.Where(c => !exclude.Contains(c.Profile.Id)).ToList();
        // Tout a déjà été joué : on recommence plutôt que de s'arrêter.
        if (pool.Count == 0) pool = candidates.ToList();
        var order = pool.Select(c => (c, Key: Math.Pow(rng.NextDouble(), 1 / Math.Max(1e-6, c.Score * c.Score * c.Score))))
            .OrderByDescending(x => x.Key).Select(x => x.c).ToList();

        var picked = new List<TrackProfile>();
        var perArtist = new Dictionary<Guid, int>();
        foreach (var pass in new[] { 2, int.MaxValue })
        {
            foreach (var c in order)
            {
                if (picked.Count >= count) break;
                if (picked.Contains(c.Profile)) continue;
                if (perArtist.GetValueOrDefault(c.Profile.ArtistId) >= pass) continue;
                if (pass == 2 && picked.Count > 0 && picked[^1].ArtistId == c.Profile.ArtistId) continue;
                picked.Add(c.Profile);
                perArtist[c.Profile.ArtistId] = perArtist.GetValueOrDefault(c.Profile.ArtistId) + 1;
            }
        }
        return picked.Select(p => p.Id).ToList();
    }

    private async Task<IReadOnlyList<TrackProfile>> ProfilesAsync(SonNuageDbContext db, CancellationToken ct)
    {
        if (cache.TryGetValue(ProfilesKey, out IReadOnlyList<TrackProfile>? known) && known is not null) return known;
        var profiles = await LoadProfilesAsync(db, ct);
        cache.Set(ProfilesKey, profiles, ProfilesFor);
        return profiles;
    }

    /// <summary>Profils de tous les morceaux, lus en base (sans cache).</summary>
    public static async Task<IReadOnlyList<TrackProfile>> LoadProfilesAsync(SonNuageDbContext db, CancellationToken ct)
    {
        var tracks = await db.Tracks.AsNoTracking()
            .Select(t => new
            {
                t.Id, t.ArtistId, t.AlbumId, Cover = t.Album.CoverPath != null || t.Album.HasEmbeddedCover, t.DurationSeconds, t.Title,
                Album = t.Album.Title, t.Genre, t.Year,
            })
            .ToListAsync(ct);
        var feels = await db.AudioFeatures.AsNoTracking().Where(f => f.Error == null)
            .Select(f => new
            {
                f.TrackId, Feel = AudioFeel.FromAnalysis(f.Bpm, f.Danceability, f.Energy, f.Valence, f.Happy, f.Sad, f.Relaxed, f.Aggressive, f.Party,
                    f.Acoustic, f.Electronic, f.Instrumental),
            })
            .ToDictionaryAsync(f => f.TrackId, f => f.Feel, ct);
        var tags = (await db.TrackTags.AsNoTracking().Select(t => new { t.TrackId, t.Name }).ToListAsync(ct))
            .GroupBy(t => t.TrackId).ToDictionary(g => g.Key, g => g.Select(t => t.Name).ToList());
        return tracks
            .Select(t => TrackProfile.Create(t.Id, t.ArtistId, t.AlbumId, t.Cover, t.DurationSeconds, t.Title, t.Album, t.Genre,
                tags.GetValueOrDefault(t.Id), feels.GetValueOrDefault(t.Id), t.Year))
            .ToList();
    }

    /// <summary>À appeler quand les morceaux, leurs tags ou leur analyse changent beaucoup.</summary>
    public void Forget()
    {
        cache.Remove(ProfilesKey);
        cache.Remove(GenresKey);
    }

    /// <summary>Genres de la bibliothèque, tels qu'écrits dans les fichiers.</summary>
    private async Task<IReadOnlyList<string>> GenresAsync(SonNuageDbContext db, CancellationToken ct)
    {
        if (cache.TryGetValue(GenresKey, out IReadOnlyList<string>? known) && known is not null) return known;
        IReadOnlyList<string> genres = await db.Tracks.Where(t => t.Genre != null && t.Genre != "").Select(t => t.Genre!).Distinct().ToListAsync(ct);
        cache.Set(GenresKey, genres, ProfilesFor);
        return genres;
    }

    private static async Task<Personal> PersonalAsync(SonNuageDbContext db, Guid userId, CancellationToken ct)
    {
        var liked = (await db.Favorites.Where(f => f.UserId == userId).Select(f => f.TrackId).ToListAsync(ct)).ToHashSet();
        var ratings = await db.Ratings.Where(r => r.UserId == userId).ToDictionaryAsync(r => r.ItemId, r => r.Value, ct);
        return new Personal(liked, ratings);
    }

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s) h = unchecked(h * 31 + c);
        return h;
    }
}
