using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

/// <summary>
/// Extrait de 30 s d'une parution (nouveautés) pas encore dans la bibliothèque : la parution est cherchée sur YouTube
/// (« artiste titre », comme les titres de la découverte) à la première écoute, puis son extrait suit le chemin
/// habituel (<see cref="PreviewService"/>, cache disque). La vidéo trouvée est retenue en mémoire : après un
/// redémarrage, la recherche est refaite une fois, l'extrait lui reste sur le disque.
/// </summary>
/// <remarks>Donne aussi les titres de la parution (Deezer), retenus de même, pour la fenêtre qui la détaille.</remarks>
public sealed class ReleasePreviews(YtDlp ytDlp, PreviewService previews, DeezerClient deezer, IOptions<SonNuageOptions> options)
{
    private readonly ConcurrentDictionary<Guid, (string VideoId, double? Duration)> _videos = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<TopRecording>> _tracks = new();

    /// <summary>Titres de la parution, dans l'ordre ; aucun si Deezer ne la connaît pas (alors redemandé la fois suivante).</summary>
    public async Task<IReadOnlyList<TopRecording>> TracksAsync(Guid releaseGroupId, string artist, string title, CancellationToken ct)
    {
        if (_tracks.TryGetValue(releaseGroupId, out var known)) return known;
        var found = await deezer.AlbumTracksAsync(artist, title, ct);
        if (found.Count > 0) _tracks[releaseGroupId] = found;
        return found;
    }

    /// <summary>Vidéo d'une parution, déjà connue.</summary>
    internal void Remember(Guid releaseGroupId, string videoId, double? duration) => _videos[releaseGroupId] = (videoId, duration);

    /// <summary>Chemin de l'extrait ; null si la parution n'est pas trouvée sur YouTube.</summary>
    public async Task<string?> GetAsync(Guid releaseGroupId, string artist, string title, CancellationToken ct)
    {
        if (!_videos.TryGetValue(releaseGroupId, out var video))
        {
            var best = (await ytDlp.MatchAsync(new MatchQuery(artist, title, null), options.Value.MatchOfficialMinScore, ct)).FirstOrDefault();
            if (best is not { Score: >= DiscoveryService.MinMatchScore }) return null;
            video = (best.Entry.Id, best.Entry.DurationSeconds);
            _videos[releaseGroupId] = video;
        }
        return await previews.GetAsync(video.VideoId, video.Duration, ct);
    }
}
