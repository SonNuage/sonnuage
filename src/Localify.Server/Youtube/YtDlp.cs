using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Localify.Server.Streaming;

namespace Localify.Server.Youtube;

public enum YtDlpFailure
{
    /// <summary>YouTube refuse pour l'instant (trop de requêtes, connexion demandée) : réessayer plus tard.</summary>
    Throttled,
    /// <summary>Vidéo privée, supprimée, bloquée : inutile de réessayer.</summary>
    Unavailable,
    Other,
}

public sealed class YtDlpException(YtDlpFailure kind, string message) : Exception(message)
{
    public YtDlpFailure Kind { get; } = kind;
}

/// <summary>Entrée d'une recherche, d'une playlist ou d'une chaîne (sans téléchargement).</summary>
public sealed record YtEntry(string Id, string Title, string? Channel, double? DurationSeconds, string? Thumbnail);

public sealed record YtListing(string Kind, string Title, string? Channel, IReadOnlyList<YtEntry> Entries);

/// <summary>Résultat d'un téléchargement : le fichier audio et les métadonnées de la vidéo (info.json de yt-dlp).</summary>
public sealed record YtDownload(string AudioPath, JsonObject Info);

/// <summary>Appels à yt-dlp. Les arguments passent par ArgumentList (jamais par une ligne de commande concaténée).</summary>
public sealed partial class YtDlp(YoutubeTools tools, FfmpegLocator ffmpeg, YoutubeSettingsStore settings, ILogger<YtDlp> logger)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>Recherche YouTube (vidéos) ou YouTube Music (titres, aux métadonnées propres).</summary>
    public Task<YtListing> SearchAsync(string query, bool music, int limit, CancellationToken ct) =>
        ListAsync(music
            ? $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}#songs"
            : $"ytsearch{limit}:{query}", limit, ct);

    /// <summary>Lit une vidéo, une playlist ou une chaîne sans rien télécharger.</summary>
    public async Task<YtListing> ListAsync(string url, int? limit, CancellationToken ct)
    {
        var args = new List<string> { "--flat-playlist", "--dump-single-json", "--no-warnings" };
        if (limit is { } l) args.AddRange(["--playlist-end", l.ToString(CultureInfo.InvariantCulture)]);
        args.Add(url);
        var json = JsonNode.Parse(await RunAsync(args, null, ListTimeout, ct))!.AsObject();

        if (json["_type"]?.GetValue<string>() is "playlist")
        {
            var entries = (json["entries"]?.AsArray() ?? [])
                .Select(e => e?.AsObject())
                .OfType<JsonObject>()
                .Where(e => e["id"]?.GetValue<string>() is { Length: 11 } && e["ie_key"]?.GetValue<string>() is null or "Youtube")
                .Select(ToEntry)
                .ToList();
            return new YtListing("playlist", Str(json, "title") ?? url, Str(json, "channel") ?? Str(json, "uploader"), entries);
        }
        return new YtListing("video", Str(json, "title") ?? url, Str(json, "channel"), [ToEntry(json)]);
    }

    /// <summary>
    /// Télécharge la meilleure piste audio telle quelle (opus ou AAC : aucun réencodage). Sans ffmpeg pour
    /// sortir l'opus de son conteneur webm, on se contente de l'AAC (m4a), lisible directement.
    /// </summary>
    public async Task<YtDownload> DownloadAsync(string videoId, string workDir, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--newline", "--no-mtime", "--no-part",
            "-f", ffmpeg.Available ? "bestaudio[acodec=opus]/bestaudio[ext=m4a]/bestaudio" : "bestaudio[ext=m4a]/bestaudio",
            "--write-info-json",
            "--progress-template", "download:[localify] %(progress._percent_str)s",
            "-o", Path.Combine(workDir, "%(id)s.%(ext)s"),
            WatchUrl(videoId),
        };
        await RunAsync(args, line =>
        {
            if (!line.StartsWith("[localify]", StringComparison.Ordinal)) return;
            if (double.TryParse(line["[localify]".Length..].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                progress?.Invoke(percent);
        }, DownloadTimeout, ct);

        var infoPath = Path.Combine(workDir, $"{videoId}.info.json");
        var audio = Directory.EnumerateFiles(workDir, $"{videoId}.*")
            .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new YtDlpException(YtDlpFailure.Other, "yt-dlp n'a produit aucun fichier audio.");
        var info = JsonNode.Parse(await File.ReadAllTextAsync(infoPath, ct))!.AsObject();
        return new YtDownload(audio, info);
    }

    private async Task<string> RunAsync(List<string> args, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        var exe = tools.YtDlp ?? throw new YtDlpException(YtDlpFailure.Other, "yt-dlp introuvable : installez-le ou utilisez « Mettre à jour yt-dlp » dans l'admin.");
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        if (tools.Deno is { } deno) { start.ArgumentList.Add("--js-runtimes"); start.ArgumentList.Add($"deno:{deno}"); }
        if (ffmpeg.Path is { } ff && File.Exists(ff)) { start.ArgumentList.Add("--ffmpeg-location"); start.ArgumentList.Add(ff); }
        if (settings.CookiesPath is { } cookies) { start.ArgumentList.Add("--cookies"); start.ArgumentList.Add(cookies); }
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new YtDlpException(YtDlpFailure.Other, "Impossible de lancer yt-dlp.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var kill = timeoutCts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (onLine is null) stdout.AppendLine(line);
                else onLine(line);
            }
        }, CancellationToken.None);
        var readErr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line) stderr.AppendLine(line);
        }, CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(readOut, readErr);

        ct.ThrowIfCancellationRequested();
        if (timeoutCts.IsCancellationRequested) throw new YtDlpException(YtDlpFailure.Other, "yt-dlp n'a pas terminé à temps.");
        if (process.ExitCode != 0)
        {
            var error = string.Join('\n', stderr.ToString().Split('\n').Where(l => l.StartsWith("ERROR", StringComparison.Ordinal))).Trim();
            if (error.Length == 0) error = stderr.ToString().Trim();
            logger.LogWarning("yt-dlp a échoué ({Code}) : {Error}", process.ExitCode, error);
            throw new YtDlpException(Classify(error), Shorten(error));
        }
        return stdout.ToString();
    }

    internal static YtDlpFailure Classify(string error)
    {
        if (ThrottledPattern().IsMatch(error)) return YtDlpFailure.Throttled;
        if (UnavailablePattern().IsMatch(error)) return YtDlpFailure.Unavailable;
        return YtDlpFailure.Other;
    }

    [GeneratedRegex(@"HTTP Error 429|Too Many Requests|Sign in to confirm|confirm you.re not a bot|rate.limit|try again later", RegexOptions.IgnoreCase)]
    private static partial Regex ThrottledPattern();

    [GeneratedRegex(@"Video unavailable|Private video|has been removed|account associated with this video has been terminated|not available in your country|members-only|This video is unavailable|copyright", RegexOptions.IgnoreCase)]
    private static partial Regex UnavailablePattern();

    private static string Shorten(string error) => (error.Length > 600 ? error[..600] + "…" : error).Replace("ERROR: ", "");

    private static YtEntry ToEntry(JsonObject e) => new(
        Str(e, "id")!,
        Str(e, "title") ?? Str(e, "id")!,
        Str(e, "channel") ?? Str(e, "uploader") ?? Str(e, "artist"),
        e["duration"]?.GetValueKind() is JsonValueKind.Number ? e["duration"]!.GetValue<double>() : null,
        // Les résultats YouTube Music « à plat » n'ont pas de miniature : celle de la vidéo existe toujours.
        BestThumbnail(e) ?? $"https://i.ytimg.com/vi/{Str(e, "id")}/hqdefault.jpg");

    internal static string? BestThumbnail(JsonObject e)
    {
        var thumbs = e["thumbnails"]?.AsArray().Select(t => t?.AsObject()).OfType<JsonObject>().ToList() ?? [];
        var best = thumbs
            .Where(t => Str(t, "url") is not null)
            .OrderByDescending(t => (t["width"]?.GetValueKind() == JsonValueKind.Number ? t["width"]!.GetValue<int>() : 0) * (t["height"]?.GetValueKind() == JsonValueKind.Number ? t["height"]!.GetValue<int>() : 0))
            .ThenByDescending(t => t["preference"]?.GetValueKind() == JsonValueKind.Number ? t["preference"]!.GetValue<int>() : 0)
            .FirstOrDefault();
        return best is null ? Str(e, "thumbnail") : Str(best, "url");
    }

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
