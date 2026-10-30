namespace SonNuage.Server.Streaming;

/// <summary>
/// Envoie un fichier en cours d'écriture par ffmpeg, en suivant sa croissance jusqu'à la fin du transcodage.
/// Pas de Content-Length (taille finale inconnue) : réponse « chunked », ce que Cloudflare relaie sans bufferiser.
/// </summary>
public sealed class GrowingFileResult(TranscodeJob job, string contentType) : IResult
{
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public async Task ExecuteAsync(HttpContext http)
    {
        var ct = http.RequestAborted;
        var response = http.Response;
        response.ContentType = contentType;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.CacheControl = "private, no-store";

        // Attente d'un créneau d'encodage libre : ne bloque pas plus que le délai d'inactivité.
        await job.Started.WaitAsync(StallTimeout, ct);
        if (job.Completion is { IsCompleted: true, Result: false })
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        if (HttpMethods.IsHead(http.Request.Method)) return;

        await using var source = await OpenAsync(ct);
        if (source is null)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        await response.StartAsync(ct);
        var buffer = new byte[BufferSize];
        var lastProgress = DateTime.UtcNow;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                lastProgress = DateTime.UtcNow;
                continue;
            }

            if (job.Completion.IsCompleted)
            {
                // Dernier passage : ce qu'ffmpeg a écrit entre notre lecture et sa sortie.
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                if (!job.Completion.Result) http.Abort();
                return;
            }

            if (DateTime.UtcNow - lastProgress > StallTimeout)
            {
                http.Abort();
                return;
            }
            await response.Body.FlushAsync(ct);
            await Task.Delay(PollDelay, ct);
        }
    }

    private async Task<FileStream?> OpenAsync(CancellationToken ct)
    {
        // ffmpeg crée le fichier juste après son démarrage.
        for (var i = 0; i < 100; i++)
        {
            foreach (var path in new[] { job.TempPath, job.FinalPath })
            {
                try
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            if (job.Completion is { IsCompleted: true, Result: false }) return null;
            await Task.Delay(PollDelay, ct);
        }
        return null;
    }
}
