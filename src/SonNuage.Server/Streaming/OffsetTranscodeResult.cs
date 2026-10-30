using System.Diagnostics;
using SonNuage.Server.Data;

namespace SonNuage.Server.Streaming;

/// <summary>
/// Encode une piste à partir d'une position et l'envoie directement (sortie standard d'ffmpeg, réponse « chunked »).
/// Sert les sauts dans une piste dont l'encodage en cache n'est pas fini : sans taille connue, le navigateur ne peut
/// pas demander de plage d'octets et resterait limité à ce qu'il a déjà reçu.
/// </summary>
public sealed class OffsetTranscodeResult(TranscodeService transcoder, Track track, TranscodeProfile profile, double start) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        var ct = http.RequestAborted;
        var response = http.Response;
        response.ContentType = profile.ContentType;
        response.Headers.CacheControl = "private, no-store";
        response.Headers["X-SonNuage-Start"] = start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (HttpMethods.IsHead(http.Request.Method)) return;

        await transcoder.WaitSlotAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(transcoder.FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in profile.FfmpegArguments(track.FilePath, "pipe:1", track.SampleRate, start)) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg n'a pas démarré");
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await response.StartAsync(ct);
                await process.StandardOutput.BaseStream.CopyToAsync(response.Body, 64 * 1024, ct);
                await process.WaitForExitAsync(ct);
            }
            finally
            {
                // Le lecteur a sauté ailleurs ou changé de piste : inutile de finir l'encodage.
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            transcoder.ReleaseSlot();
        }
    }
}
