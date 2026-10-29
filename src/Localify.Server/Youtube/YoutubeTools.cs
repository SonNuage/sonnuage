using Localify.Contracts.Localization;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Localify.Contracts;
using Microsoft.Extensions.Options;

namespace Localify.Server.Youtube;

/// <summary>
/// Localise yt-dlp, Deno (moteur JavaScript qu'exige désormais YouTube) et fpcalc, et met à jour les deux premiers
/// depuis leurs releases GitHub officielles, sommes SHA-256 vérifiées. YouTube change souvent : un yt-dlp de
/// quelques semaines peut déjà échouer, d'où la mise à jour en un clic depuis l'admin.
/// Ordre de recherche : dossier des outils (mises à jour) → chemin configuré → PATH.
/// </summary>
public sealed class YoutubeTools(IOptions<LocalifyOptions> options, IHttpClientFactory httpFactory, ILogger<YoutubeTools> logger)
{
    public const string HttpClientName = "tools";
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private ToolsStatusDto? _status;

    public string? YtDlp => Find(Windows ? "yt-dlp.exe" : "yt-dlp", options.Value.YtDlpPath, "yt-dlp");
    public string? Deno => Find(Windows ? "deno.exe" : "deno", options.Value.DenoPath, "deno");
    public string? Fpcalc => Find(Windows ? "fpcalc.exe" : "fpcalc", options.Value.FpcalcPath, "fpcalc");

    public async Task<ToolsStatusDto> StatusAsync(bool refresh = false)
    {
        if (_status is not null && !refresh) return _status;
        return _status = new ToolsStatusDto(
            await VersionAsync(YtDlp, "--version"),
            await VersionAsync(Deno, "--version"),
            await VersionAsync(Fpcalc, "-version"));
    }

    /// <summary>Télécharge la dernière version de yt-dlp (et de Deno s'il manque ou si <paramref name="deno"/>).</summary>
    public async Task<ToolsStatusDto> UpdateAsync(bool deno, CancellationToken ct)
    {
        await _updateLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(options.Value.ToolsPath);
            await UpdateYtDlpAsync(ct);
            if (deno || Deno is null) await UpdateDenoAsync(ct);
            return await StatusAsync(refresh: true);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task UpdateYtDlpAsync(CancellationToken ct)
    {
        var asset = (Windows, RuntimeInformation.OSArchitecture) switch
        {
            (true, _) => "yt-dlp.exe",
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => "yt-dlp_linux_aarch64",
            (false, _) when OperatingSystem.IsLinux() => "yt-dlp_linux",
            _ => "yt-dlp_macos",
        };
        const string release = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
        var http = httpFactory.CreateClient(HttpClientName);
        var sums = await http.GetStringAsync(release + "SHA2-256SUMS", ct);
        var expected = sums.Split('\n').Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim() == asset)?[0]
            ?? throw new InvalidOperationException(Tx.Key("errors.yt.checksumMissing", ("asset", asset)));
        var bytes = await http.GetByteArrayAsync(release + asset, ct);
        Verify(bytes, expected, asset);
        await InstallAsync(bytes, Windows ? "yt-dlp.exe" : "yt-dlp", ct);
    }

    private async Task UpdateDenoAsync(CancellationToken ct)
    {
        var target = (Windows, RuntimeInformation.OSArchitecture) switch
        {
            (true, _) => "x86_64-pc-windows-msvc",
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => "aarch64-unknown-linux-gnu",
            (false, _) when OperatingSystem.IsLinux() => "x86_64-unknown-linux-gnu",
            (false, Architecture.Arm64) => "aarch64-apple-darwin",
            _ => "x86_64-apple-darwin",
        };
        var asset = $"deno-{target}.zip";
        var url = $"https://github.com/denoland/deno/releases/latest/download/{asset}";
        var http = httpFactory.CreateClient(HttpClientName);
        // « hash  fichier » sous Linux/macOS, sortie de Get-FileHash (« Hash : … ») pour Windows.
        var expected = System.Text.RegularExpressions.Regex.Match(await http.GetStringAsync(url + ".sha256sum", ct), @"\b[0-9a-fA-F]{64}\b") is { Success: true } m
            ? m.Value
            : throw new InvalidOperationException(Tx.Key("errors.yt.checksumUnreadable", ("asset", asset)));
        var zip = await http.GetByteArrayAsync(url, ct);
        Verify(zip, expected, asset);
        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = archive.Entries.First(e => e.Name is "deno" or "deno.exe");
        using var buffer = new MemoryStream();
        await using (var s = entry.Open()) await s.CopyToAsync(buffer, ct);
        await InstallAsync(buffer.ToArray(), entry.Name, ct);
    }

    private static void Verify(byte[] bytes, string expectedHex, string asset)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Tx.Key("errors.yt.checksumMismatch", ("asset", asset)));
    }

    /// <summary>Écrit à côté puis remplace, pour ne jamais laisser un exécutable à moitié écrit.</summary>
    private async Task InstallAsync(byte[] bytes, string name, CancellationToken ct)
    {
        var final = Path.Combine(options.Value.ToolsPath, name);
        var temp = final + ".new";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        File.Move(temp, final, overwrite: true);
        logger.LogInformation("{Tool} mis à jour : {Version}", name, await VersionAsync(final, "--version"));
    }

    private string? Find(string fileName, string? configured, string command)
    {
        var updated = Path.Combine(options.Value.ToolsPath, fileName);
        if (File.Exists(updated)) return updated;
        if (!string.IsNullOrWhiteSpace(configured) && (File.Exists(configured) || configured == command && OnPath(command))) return configured;
        return OnPath(command) ? command : null;
    }

    private static bool OnPath(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, command)) || Windows && File.Exists(Path.Combine(dir, command + ".exe")));

    private static async Task<string?> VersionAsync(string? path, string argument)
    {
        if (path is null) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, argument)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            // « 2026.10.15 », « deno 2.5.4 (stable, …) », « fpcalc version 1.5.1 »
            var words = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().Split(' ');
            return words switch
            {
                null or [] => null,
                ["fpcalc", ..] => words[^1],
                ["deno", var v, ..] => v,
                [var v, ..] => v,
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }
}
