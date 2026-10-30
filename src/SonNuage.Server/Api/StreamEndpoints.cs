using SonNuage.Contracts.Localization;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace SonNuage.Server.Api;

public static class StreamEndpoints
{
    /// <summary>Attente max de la fin d'un encodage quand un client saute au milieu d'une piste en cours de transcodage.</summary>
    private static readonly TimeSpan SeekWaitTimeout = TimeSpan.FromSeconds(60);

    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/tracks/{id:guid}/stream", ["GET", "HEAD"], StreamAsync)
            .RequireAuthorization(AuthConstants.MediaPolicy)
            .WithTags("Media");
    }

    private static async Task<IResult> StreamAsync(
        Guid id,
        string? quality,
        double? start,
        HttpContext http,
        SonNuageDbContext db,
        TranscodeService transcoder,
        CancellationToken ct)
    {
        if (!StreamQualities.TryParse(quality, out var requested))
            return Results.Problem(Tx.Key("errors.unknownQuality", ("quality", quality)), statusCode: StatusCodes.Status400BadRequest);

        var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (track is null || !File.Exists(track.FilePath)) return Results.NotFound();

        http.Response.Headers.CacheControl = "private, no-store";
        // Indique au client ce qu'il reçoit réellement (la qualité peut être rabattue sur l'original).
        var profile = transcoder.Available ? TranscodeProfile.Resolve(track, requested) : null;
        http.Response.Headers["X-SonNuage-Quality"] = profile?.Name ?? "original";
        http.Response.Headers["X-SonNuage-Duration"] = track.DurationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        // Saut au-delà de ce que le navigateur a déjà reçu d'un flux en cours d'encodage (sans Range possible) :
        // le lecteur redemande la piste à partir de ce point, encodée à part sans passer par le cache.
        if (start is > 0 && transcoder.Available && start < track.DurationSeconds)
            return new OffsetTranscodeResult(transcoder, track, profile ?? TranscodeProfile.For(StreamQuality.High), start.Value);

        if (profile is null)
            return Results.File(track.FilePath, LibraryFiles.ContentType(track.FilePath), lastModified: track.FileModifiedUtc, enableRangeProcessing: true);

        var handle = transcoder.Get(track, profile);
        if (handle.IsCached || handle.Job!.Completion is { IsCompletedSuccessfully: true, Result: true })
            return Results.File(handle.FinalPath, profile.ContentType, enableRangeProcessing: true);

        // Saut en avant pendant l'encodage : l'encodage va vite (> 50× le temps réel), on attend le fichier complet
        // pour répondre proprement en 206 avec Content-Range.
        if (RequestsOffset(http.Request))
        {
            try
            {
                var ok = await handle.Job.Completion.WaitAsync(SeekWaitTimeout, ct);
                if (ok) return Results.File(handle.FinalPath, profile.ContentType, enableRangeProcessing: true);
            }
            catch (TimeoutException)
            {
            }
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return new GrowingFileResult(handle.Job, profile.ContentType);
    }

    private static bool RequestsOffset(HttpRequest request)
    {
        var range = request.GetTypedHeaders().Range;
        if (range is null || range.Ranges.Count == 0) return false;
        var first = range.Ranges.First();
        return first.From is > 0 || first.From is null;
    }
}
