using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;

namespace SonNuage.Server.ListenBrainz;

/// <summary>
/// Recommandations personnelles de ListenBrainz (filtrage collaboratif) pour un compte relié. Celles qui sont dans
/// la bibliothèque (même identifiant d'enregistrement MusicBrainz) vont à tous ; les autres, seulement aux comptes
/// qui peuvent demander un téléchargement. Gardées une heure : ListenBrainz ne les recalcule que de temps en temps.
/// </summary>
public sealed class ListenBrainzRecommendations(ListenBrainzApi api, IMemoryCache cache, ILogger<ListenBrainzRecommendations> logger)
{
    private const int Fetched = 100;
    private const int MaxTracks = 20;
    private const int MaxOutside = 12;
    private static readonly TimeSpan Keep = TimeSpan.FromHours(1);
    private static readonly ListenBrainzRecommendationsDto None = new([], []);

    public async Task<ListenBrainzRecommendationsDto> ForAsync(SonNuageDbContext db, Guid userId, bool canRequest, CancellationToken ct)
    {
        // Pas de jeton enregistré : rien ne part vers ListenBrainz.
        var userName = await db.Users.Where(u => u.Id == userId && u.ListenBrainzToken != null).Select(u => u.ListenBrainzUserName).SingleOrDefaultAsync(ct);
        if (userName is null) return None;
        var key = (nameof(ListenBrainzRecommendations), userId, userName);
        if (!cache.TryGetValue(key, out IReadOnlyList<Recommendation>? recommendations) || recommendations is null)
        {
            try
            {
                recommendations = await api.RecommendationsAsync(userName, Fetched, ct);
            }
            catch (ListenBrainzUnavailableException ex)
            {
                logger.LogInformation("Recommandations ListenBrainz indisponibles : {Message}", ex.Message);
                return None;
            }
            cache.Set(key, recommendations, Keep);
        }

        var mbids = recommendations.Select(r => (Guid?)r.RecordingMbid).ToList();
        var inLibrary = await db.Tracks.Where(t => mbids.Contains(t.MusicBrainzRecordingId))
            .Select(t => new { t.Id, Mbid = t.MusicBrainzRecordingId!.Value }).ToListAsync(ct);
        // Un enregistrement présent en plusieurs exemplaires : un seul suffit.
        var trackByMbid = inLibrary.GroupBy(t => t.Mbid).ToDictionary(g => g.Key, g => g.First().Id);
        var trackIds = recommendations.Where(r => trackByMbid.ContainsKey(r.RecordingMbid)).Select(r => trackByMbid[r.RecordingMbid]).Take(MaxTracks).ToList();
        var tracks = await LibraryEndpoints.TracksInOrderAsync(db, userId, trackIds, ct);
        if (!canRequest) return new ListenBrainzRecommendationsDto(tracks, []);

        var outside = recommendations.Where(r => !trackByMbid.ContainsKey(r.RecordingMbid)).Take(MaxOutside).Select(r => r.RecordingMbid).ToList();
        return new ListenBrainzRecommendationsDto(tracks, await DescribeAsync(outside, ct));
    }

    private async Task<IReadOnlyList<ExternalRecordingDto>> DescribeAsync(IReadOnlyList<Guid> mbids, CancellationToken ct)
    {
        if (mbids.Count == 0) return [];
        var key = (nameof(DescribeAsync), string.Join(',', mbids));
        if (!cache.TryGetValue(key, out IReadOnlyDictionary<Guid, ExternalRecording>? found) || found is null)
        {
            try
            {
                found = await api.RecordingsAsync(mbids, ct);
            }
            catch (ListenBrainzUnavailableException ex)
            {
                logger.LogInformation("Métadonnées ListenBrainz indisponibles : {Message}", ex.Message);
                return [];
            }
            cache.Set(key, found, Keep);
        }
        return mbids.Where(found.ContainsKey).Select(id => found[id])
            .Select(r => new ExternalRecordingDto(r.RecordingMbid.ToString(), r.Title, r.Artist, r.Release)).ToList();
    }
}
