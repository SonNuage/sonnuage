using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Localify.Server.Streaming;

public sealed class FfmpegLocator
{
    private readonly Lazy<string?> _path;

    public FfmpegLocator(IOptions<LocalifyOptions> options, ILogger<FfmpegLocator> logger)
    {
        _path = new Lazy<string?>(() =>
        {
            var configured = options.Value.FfmpegPath;
            foreach (var candidate in new[] { configured, "ffmpeg" }.Distinct())
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(candidate, "-version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    });
                    if (p is null) continue;
                    p.StandardOutput.ReadToEnd();
                    if (p.WaitForExit(5000) && p.ExitCode == 0)
                    {
                        logger.LogInformation("ffmpeg disponible : {Path}", candidate);
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
            logger.LogWarning("ffmpeg introuvable : transcodage et miniatures désactivés, les fichiers originaux seront servis");
            return null;
        });
    }

    public string? Path => _path.Value;
    public bool Available => _path.Value is not null;
}
