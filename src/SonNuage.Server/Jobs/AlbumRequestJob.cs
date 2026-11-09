using System.Text.Json;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Jobs;

public sealed record AlbumRequestPayload(Guid UserId, string Artist, string Album);

/// <summary>
/// « Prendre tout l'album » : les titres de l'album (Deezer, dans l'ordre) sont retrouvés sur YouTube par la recherche
/// par correspondance (chaîne officielle d'abord), puis demandés un par un au nom du contributeur, avec ses règles
/// habituelles (quota du jour, approbation). Un titre déjà sur le serveur ou déjà demandé est passé.
/// </summary>
public sealed class AlbumRequestJob(DeezerClient deezer, YtDlp ytDlp, YoutubeTools tools, IOptions<SonNuageOptions> options, TimeProvider time) : IJobHandler
{
    public JobKind Kind => JobKinds.RequestAlbum;

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        if (ctx.Payload.Deserialize<AlbumRequestPayload>(JobContext.Json) is not { } payload) return;
        var db = ctx.Services.GetRequiredService<SonNuageDbContext>();
        var importer = ctx.Services.GetRequiredService<YoutubeImporter>();
        if (await db.Users.FindAsync([payload.UserId], ct) is not { } user) return;
        if (tools.YtDlp is null)
        {
            ctx.Warn(Tx.Key("errors.yt.missing"));
            return;
        }

        var tracks = await deezer.AlbumTracksAsync(payload.Artist, payload.Album, ct);
        if (tracks.Count == 0)
        {
            ctx.Warn(Tx.Key("jobs.requests.albumNotFound", ("artist", payload.Artist), ("album", payload.Album)));
            return;
        }

        int created = 0, skipped = 0, missing = 0;
        for (var i = 0; i < tracks.Count; i++)
        {
            ctx.Report((double)i / tracks.Count);
            var track = tracks[i];
            RankedEntry? best;
            try
            {
                best = (await ytDlp.MatchAsync(new MatchQuery(payload.Artist, track.Title, track.LengthSeconds), options.Value.MatchOfficialMinScore, ct))
                    .FirstOrDefault();
            }
            catch (YtDlpException ex) when (ex.Kind == YtDlpFailure.Throttled)
            {
                ctx.Warn(Tx.Key("jobs.discovery.throttled"));
                break;
            }
            catch (YtDlpException ex)
            {
                ctx.Warn(Tx.Key("jobs.discovery.matchFailed", ("title", track.Title), ("message", ex.Message)));
                missing++;
                continue;
            }
            if (best is not { Score: >= DiscoveryService.MinMatchScore })
            {
                ctx.Warn(Tx.Key("jobs.requests.albumTrackNotFound", ("title", track.Title)));
                missing++;
                continue;
            }

            var request = new CreateDownloadRequest(best.Entry.Id, best.Entry.Title, payload.Artist, track.LengthSeconds ?? best.Entry.DurationSeconds, payload.Album);
            var (outcome, _) = await DownloadRequestEndpoints.CreateAsync(db, importer, user, request, time.GetUtcNow(), ct);
            if (outcome == DownloadRequestEndpoints.CreateOutcome.Quota)
            {
                ctx.Warn(Tx.Key("errors.requests.quota", ("limit", user.RequestsPerDay)));
                break;
            }
            if (outcome == DownloadRequestEndpoints.CreateOutcome.Created) created++;
            else skipped++;
        }
        ctx.Report(1);
        ctx.Info(Tx.Key("jobs.requests.albumDone", ("created", (object)created), ("skipped", skipped), ("missing", missing), ("count", tracks.Count)));
    }
}
