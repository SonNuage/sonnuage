using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Localify.Contracts;

namespace Localify.Server.Youtube;

public sealed record MetadataResolution(MetadataCandidateDto Best, IReadOnlyList<MetadataCandidateDto> Candidates)
{
    public double Confidence => Best.Score;
}

/// <summary>
/// Reconstitue les métadonnées d'une vidéo en croisant les sources, de la plus sûre à la moins sûre :
/// <list type="number">
/// <item>empreinte acoustique AcoustID (si clé et fpcalc) : reconnaît le morceau quel que soit le titre de la vidéo ;</item>
/// <item>métadonnées YouTube Music (piste, artistes, album, année) que yt-dlp extrait des titres « auto-générés » ;</item>
/// <item>recherche MusicBrainz sur artiste + titre (déduits du titre de la vidéo si besoin), notée sur le titre,
///       l'artiste et la durée ; l'édition retenue privilégie l'album studio officiel le plus ancien ;</item>
/// <item>à défaut, le titre de la vidéo découpé « Artiste - Titre ».</item>
/// </list>
/// Chaque candidat reçoit un score de 0 à 1 ; sous le seuil réglé dans l'admin, l'import attend une vérification.
/// </summary>
public sealed class MetadataResolver(MusicBrainzClient musicBrainz, ILogger<MetadataResolver> logger)
{
    public async Task<MetadataResolution> ResolveAsync(JsonObject info, string audioPath, string? albumHint, string? acoustIdKey, CancellationToken ct)
    {
        var videoTitle = YtDlp.Str(info, "title") ?? "";
        var channel = YtDlp.Str(info, "channel") ?? YtDlp.Str(info, "uploader");
        var duration = info["duration"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number ? d.GetValue<double>() : (double?)null;
        var parsed = TitleParser.Parse(videoTitle, channel);
        var music = FromYoutubeMusic(info, albumHint);

        var queryTitle = music?.Title ?? parsed.Title;
        // Le premier artiste seulement : « A, B » ne correspond à aucun crédit MusicBrainz (« A feat. B »).
        var queryArtist = music?.AlbumArtist ?? music?.Artist ?? parsed.Artist;
        var queryAlbum = music?.Album ?? albumHint;
        var candidates = new List<MetadataCandidateDto>();

        if (music is not null) candidates.Add(music);
        candidates.Add(new MetadataCandidateDto(
            parsed.Title, parsed.Artist ?? channel ?? "Artiste inconnu", albumHint ?? parsed.Title, null, null, null,
            UploadYear(info), null, parsed.Artist is null ? 0.2 : 0.35, "titre"));

        try
        {
            if (!string.IsNullOrWhiteSpace(acoustIdKey))
            {
                var matches = await musicBrainz.AcoustIdAsync(audioPath, acoustIdKey, ct);
                var byId = matches.ToDictionary(m => m.RecordingId, m => m.Score);
                foreach (var rec in await musicBrainz.RecordingsByIdAsync(matches.Select(m => m.RecordingId).ToList(), ct))
                {
                    var text = TextScore(rec, queryTitle, queryArtist, duration);
                    var score = Math.Min(1, 0.55 * byId.GetValueOrDefault(rec.Id) + 0.25 * TitleParser.Similarity(rec.Title, queryTitle) + 0.2 * Math.Max(ArtistScore(rec.ArtistCredit, queryArtist), text));
                    candidates.Add(ToCandidate(rec, queryAlbum, score, "acoustid"));
                }
            }

            foreach (var rec in await musicBrainz.SearchRecordingsAsync(queryTitle, queryArtist, ct))
                candidates.Add(ToCandidate(rec, queryAlbum, TextScore(rec, queryTitle, queryArtist, duration), "musicbrainz"));
            // Titre de vidéo mal découpé (« Titre - Artiste ») : seconde chance dans l'autre sens.
            if (music is null && parsed.Artist is not null && candidates.Max(c => c.Score) < 0.7)
            {
                foreach (var rec in await musicBrainz.SearchRecordingsAsync(parsed.Artist, parsed.Title, ct))
                    candidates.Add(ToCandidate(rec, queryAlbum, TextScore(rec, parsed.Artist, parsed.Title, duration) * 0.95, "musicbrainz"));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Recherche de métadonnées incomplète pour « {Title} » : {Message}", videoTitle, ex.Message);
        }

        // MusicBrainz confirme YouTube Music : on garde MusicBrainz (numéro de piste, identifiants, pochette).
        if (music is not null)
        {
            candidates = candidates.Select(c => c.Source is "musicbrainz" or "acoustid"
                && TitleParser.Similarity(c.Title, music.Title) >= 0.85
                && ArtistScore(c.Artist, queryArtist) >= 0.85
                    ? c with { Score = Math.Max(c.Score, Math.Min(1, music.Score + 0.07)) }
                    : c).ToList();
        }

        var ranked = candidates
            .OrderByDescending(c => c.Score)
            .DistinctBy(c => (c.Title.ToLowerInvariant(), c.Artist.ToLowerInvariant(), c.Album?.ToLowerInvariant()))
            .Take(6)
            .Select(c => c with { Score = Math.Round(c.Score, 3) })
            .ToList();
        return new MetadataResolution(ranked[0], ranked);
    }

    /// <summary>Champs « track », « artists », « album » que yt-dlp lit sur les vidéos YouTube Music et les fiches musique.</summary>
    internal static MetadataCandidateDto? FromYoutubeMusic(JsonObject info, string? albumHint)
    {
        var track = YtDlp.Str(info, "track");
        var artists = (info["artists"] as JsonArray)?.Select(a => a?.GetValueKind() == JsonValueKind.String ? a.GetValue<string>().Trim() : null).OfType<string>().ToList();
        // YouTube Music liste parfois un fragment du nom comme second artiste (« Kevin MacLeod », « Kevin ») : on l'écarte.
        artists = artists?.Where((a, i) => a.Length > 0 && !artists.Where((b, j) => j != i && b.Length > a.Length).Any(b => b.Contains(a, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        var artist = artists is { Count: > 0 } ? string.Join(", ", artists) : YtDlp.Str(info, "artist") ?? YtDlp.Str(info, "creator");
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(artist)) return null;
        var album = YtDlp.Str(info, "album");
        var year = info["release_year"] is JsonValue y && y.GetValueKind() == JsonValueKind.Number ? y.GetValue<int>() : UploadYear(info);
        return new MetadataCandidateDto(track, artist, album ?? albumHint ?? track, artists is { Count: > 0 } ? artists[0] : null,
            null, null, year, null, album is null ? 0.75 : 0.85, "youtube-music");
    }

    private static int? UploadYear(JsonObject info) =>
        (YtDlp.Str(info, "release_date") ?? YtDlp.Str(info, "upload_date")) is { Length: >= 4 } date && int.TryParse(date[..4], out var y) ? y : null;

    internal static double TextScore(MbRecording rec, string title, string? artist, double? duration)
    {
        var t = TitleParser.Similarity(rec.Title, title);
        var a = ArtistScore(rec.ArtistCredit, artist);
        var d = (rec.LengthSeconds, duration) switch
        {
            (null, _) or (_, null) => 0.5,
            var (x, y) when Math.Abs(x.Value - y.Value) <= 3 => 1,
            var (x, y) when Math.Abs(x.Value - y.Value) <= 10 => 0.6,
            var (x, y) when Math.Abs(x.Value - y.Value) <= 30 => 0.25,
            _ => 0,
        };
        return 0.45 * t + 0.35 * a + 0.2 * d;
    }

    /// <summary>« Daft Punk » contre « Daft Punk feat. Pharrell Williams » : l'un contient l'autre, c'est le même artiste.</summary>
    private static double ArtistScore(string candidate, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0.4;
        var sim = TitleParser.Similarity(candidate, query);
        var a = Library.NameNormalizer.Key(candidate);
        var b = Library.NameNormalizer.Key(query);
        if (a.Length >= 3 && b.Length >= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) sim = Math.Max(sim, 0.9);
        return sim;
    }

    internal static MetadataCandidateDto ToCandidate(MbRecording rec, string? albumHint, double score, string source)
    {
        var release = PickRelease(rec.Releases, albumHint);
        if (release is not null && albumHint is not null && TitleParser.Similarity(release.Title, albumHint) >= 0.8) score = Math.Min(1, score + 0.05);
        var genre = rec.TopTag is null ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(rec.TopTag);
        return new MetadataCandidateDto(
            rec.Title,
            rec.ArtistCredit,
            release?.Title ?? rec.Title,
            release?.ArtistCredit ?? rec.ArtistCredit,
            release?.TrackNumber,
            release?.DiscNumber,
            Year(release?.Date) ?? Year(rec.FirstReleaseDate),
            genre,
            score,
            source,
            rec.Id,
            release?.Id,
            release?.ReleaseGroupId);
    }

    /// <summary>L'album demandé s'il existe, sinon l'album studio officiel le plus ancien, puis single / EP, compilations en dernier.</summary>
    internal static MbRelease? PickRelease(IReadOnlyList<MbRelease> releases, string? albumHint) =>
        releases
            .OrderByDescending(r => albumHint is not null && TitleParser.Similarity(r.Title, albumHint) >= 0.8)
            .ThenByDescending(r => r.Status == "Official")
            .ThenBy(r => r.SecondaryTypes.Count)
            .ThenBy(r => r.PrimaryType switch { "Album" => 0, "EP" => 1, "Single" => 2, null => 4, _ => 3 })
            .ThenBy(r => string.IsNullOrEmpty(r.Date) ? "9999" : r.Date, StringComparer.Ordinal)
            .FirstOrDefault();

    private static int? Year(string? date) => date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
}
