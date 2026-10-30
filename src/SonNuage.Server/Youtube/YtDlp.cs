using SonNuage.Contracts.Localization;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SonNuage.Server.Streaming;

namespace SonNuage.Server.Youtube;

public enum YtDlpFailure
{
    /// <summary>YouTube refuse pour l'instant (trop de requêtes, « pas un robot ») : réessayer plus tard.</summary>
    Throttled,
    /// <summary>Vidéo privée, supprimée, bloquée : inutile de réessayer.</summary>
    Unavailable,
    /// <summary>Réservée aux comptes connectés, aux majeurs ou aux membres : il faut les cookies d'un compte qui y a accès.</summary>
    NeedsAccount,
    /// <summary>Aucun des formats demandés n'est proposé : voir la liste des formats et relancer avec un autre.</summary>
    FormatUnavailable,
    Other,
}

public sealed class YtDlpException(YtDlpFailure kind, string message) : Exception(message)
{
    public YtDlpFailure Kind { get; } = kind;

    /// <summary>Pour <see cref="YtDlpFailure.FormatUnavailable"/> : tables « --list-formats » (connecté puis sans cookies).</summary>
    public string? Formats { get; init; }
}

/// <summary>Entrée d'une recherche, d'une playlist ou d'une chaîne (sans téléchargement).</summary>
/// <param name="Album">Titre de la playlist d'origine quand une chaîne est lue album par album.</param>
public sealed record YtEntry(string Id, string Title, string? Channel, double? DurationSeconds, string? Thumbnail, string? Album = null);

public sealed record YtListing(string Kind, string Title, string? Channel, IReadOnlyList<YtEntry> Entries);

/// <summary>Résultat d'un téléchargement : le fichier audio et les métadonnées de la vidéo (info.json de yt-dlp).</summary>
public sealed record YtDownload(string AudioPath, JsonObject Info);

/// <summary>Appels à yt-dlp. Les arguments passent par ArgumentList (jamais par une ligne de commande concaténée).</summary>
public sealed partial class YtDlp(YoutubeTools tools, FfmpegLocator ffmpeg, YoutubeSettingsStore settings, ILogger<YtDlp> logger)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private const string ThrottledRate = "100K";

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>Format par défaut : l'audio seul (opus, sinon AAC), à défaut une vidéo avec son dont on ne gardera que l'audio.</summary>
    public string DefaultFormat => ffmpeg.Available
        ? "bestaudio[acodec=opus]/bestaudio[ext=m4a]/bestaudio/best[acodec!=none]"
        : "bestaudio[ext=m4a]/bestaudio";

    /// <summary>Un sélecteur de format yt-dlp raisonnable (pas d'option ni de chemin glissés dedans).</summary>
    public static bool IsValidFormat(string? format) => format is not null && FormatPattern().IsMatch(format);

    [GeneratedRegex(@"^[A-Za-z0-9_+/\[\]=<>!*.,:?^$~-]{1,200}$")]
    private static partial Regex FormatPattern();

    /// <summary>Recherche YouTube (vidéos) ou YouTube Music (titres, aux métadonnées propres).</summary>
    public Task<YtListing> SearchAsync(string query, bool music, int limit, CancellationToken ct) =>
        ListAsync(music
            ? $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}#songs"
            : $"ytsearch{limit}:{query}", limit, ct);

    /// <summary>
    /// Lit une vidéo, une playlist ou une chaîne sans rien télécharger. Une chaîne peut se présenter comme une liste
    /// d'onglets (Vidéos, Sorties…) ou de playlists (« Albums et singles ») : ces sous-listes sont lues à leur tour
    /// (sauf Shorts et directs), et le titre d'une playlist devient l'album de ses vidéos.
    /// </summary>
    /// <remarks>
    /// Racine d'une chaîne d'artiste : yt-dlp n'en donne que les onglets Vidéos et Shorts, pas « Sorties » où sont
    /// rangés albums, EP et singles (titres « auto-générés » aux métadonnées propres). Si la chaîne a des sorties,
    /// ce sont elles qu'on lit, album par album ; sinon (chaîne sans discographie), ses vidéos.
    /// </remarks>
    public async Task<YtListing> ListAsync(string url, int? limit, CancellationToken ct)
    {
        if (YoutubeSyncService.IsChannelRoot(url) && await ReleasesAsync(url, limit, ct) is { } releases) return releases;
        var json = await DumpAsync(url, limit, ct);
        if (Str(json, "_type") is not "playlist")
            return new YtListing("video", Str(json, "title") ?? url, Str(json, "channel"), [ToEntry(json)]);

        var entries = new List<YtEntry>();
        await CollectAsync(json, null, depth: 0, limit, entries, ct);
        return new YtListing("playlist", Str(json, "title") ?? url, Str(json, "channel") ?? Str(json, "uploader"),
            entries.DistinctBy(e => e.Id).ToList());
    }

    private async Task<YtListing?> ReleasesAsync(string channelUrl, int? limit, CancellationToken ct)
    {
        // music.youtube.com/@x : même identifiant ; l'onglet Sorties est sur www.youtube.com.
        var path = new Uri(channelUrl).AbsolutePath.TrimEnd('/');
        JsonObject json;
        try
        {
            json = await DumpAsync($"https://www.youtube.com{path}/releases", null, ct);
        }
        catch (YtDlpException ex) when (ex.Kind is not YtDlpFailure.Throttled)
        {
            // Pas d'onglet Sorties : chaîne ordinaire.
            logger.LogDebug("Pas de sorties pour {Url} : {Message}", channelUrl, ex.Message);
            return null;
        }
        if (json["entries"] is not JsonArray { Count: > 0 }) return null;
        var entries = new List<YtEntry>();
        await CollectAsync(json, null, depth: 0, limit, entries, ct);
        if (entries.Count == 0) return null;
        var title = Str(json, "channel") ?? Str(json, "uploader") ?? Regex.Replace(Str(json, "title") ?? channelUrl, @"\s-\s\w+$", "");
        return new YtListing("playlist", title, Str(json, "channel") ?? Str(json, "uploader"), entries.DistinctBy(e => e.Id).ToList());
    }

    private async Task<JsonObject> DumpAsync(string url, int? limit, CancellationToken ct)
    {
        var args = new List<string> { "--flat-playlist", "--dump-single-json", "--no-warnings" };
        if (limit is { } l) args.AddRange(["--playlist-end", l.ToString(CultureInfo.InvariantCulture)]);
        args.Add(url);
        return JsonNode.Parse(await RunAsync(args, null, ListTimeout, ct))!.AsObject();
    }

    private async Task CollectAsync(JsonObject playlist, string? album, int depth, int? limit, List<YtEntry> into, CancellationToken ct)
    {
        foreach (var e in (playlist["entries"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            if (limit is { } max && into.Count >= max) return;
            if (IsVideo(e))
            {
                into.Add(ToEntry(e) with { Album = album });
                continue;
            }
            // Sous-liste : onglet de chaîne ou playlist (album), déjà détaillée ou à lire.
            var url = Str(e, "url") ?? Str(e, "webpage_url");
            if (depth >= 2 || SkippedTab().IsMatch(url ?? Str(e, "title") ?? "")) continue;
            if (e["entries"] is not JsonArray && (url is null || YoutubeSyncService.NormalizeUrl(url) is null)) continue;
            try
            {
                var nested = e["entries"] is JsonArray ? e : await DumpAsync(url!, limit is { } m ? m - into.Count : null, ct);
                var isAlbum = url?.Contains("list=", StringComparison.Ordinal) == true;
                await CollectAsync(nested, isAlbum ? Str(nested, "title") ?? Str(e, "title") : album, depth + 1, limit, into, ct);
            }
            catch (YtDlpException ex) when (ex.Kind is not YtDlpFailure.Throttled)
            {
                logger.LogInformation("Sous-liste {Url} ignorée : {Message}", url, ex.Message);
            }
        }
    }

    private static bool IsVideo(JsonObject e) =>
        Str(e, "id") is { Length: 11 } && Str(e, "ie_key") is null or "Youtube" && Str(e, "_type") is null or "url" or "video";

    // Onglet par son adresse (…/shorts) ou, quand yt-dlp le détaille sur place, par son titre (« Chaîne - Shorts »).
    [GeneratedRegex(@"(/|\s-\s)(shorts|streams|live|community|posts|podcasts|courses|store|about)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SkippedTab();

    /// <summary>
    /// Télécharge la meilleure piste audio telle quelle (opus ou AAC : aucun réencodage). Sans ffmpeg pour
    /// sortir l'opus de son conteneur webm, on se contente de l'AAC (m4a), lisible directement.
    /// </summary>
    /// <remarks>
    /// Avec des cookies, on essaie d'abord connecté (vidéos réservées aux majeurs, aux inscrits, aux membres).
    /// Connecté, YouTube ne donne parfois à yt-dlp qu'un seul format : si le format demandé manque et que
    /// <paramref name="retryWithoutCookies"/>, on relance aussitôt sans les cookies. S'il manque encore,
    /// l'erreur porte la liste des formats proposés (connecté puis non).
    /// </remarks>
    public async Task<YtDownload> DownloadAsync(string videoId, string workDir, string? format, bool retryWithoutCookies,
        Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--newline", "--no-mtime", "--no-part",
            // YouTube bride parfois un flux à la vitesse de lecture (3 min pour un titre de 3 min) : sous ce débit,
            // yt-dlp redemande une adresse de flux au lieu de subir le bridage.
            "--throttled-rate", ThrottledRate,
            "-f", string.IsNullOrWhiteSpace(format) ? DefaultFormat : format.Trim(),
            "--write-info-json",
            "--progress-template", "download:[sonnuage] %(progress._percent_str)s",
            "-o", Path.Combine(workDir, "%(id)s.%(ext)s"),
            WatchUrl(videoId),
        };
        void OnLine(string line)
        {
            if (!line.StartsWith("[sonnuage]", StringComparison.Ordinal)) return;
            if (double.TryParse(line["[sonnuage]".Length..].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                progress?.Invoke(percent);
        }

        var run = await TryRunAsync(args, OnLine, DownloadTimeout, signedIn: true, ct);
        if (run.Failure is { Kind: YtDlpFailure.FormatUnavailable } && run.SignedIn && retryWithoutCookies)
        {
            logger.LogInformation("{VideoId} : format absent une fois connecté, nouvel essai sans les cookies", videoId);
            Clear(workDir, videoId);
            run = await TryRunAsync(args, OnLine, DownloadTimeout, signedIn: false, ct);
        }
        if (run.Failure is { } failure)
        {
            Clear(workDir, videoId);
            if (failure.Kind != YtDlpFailure.FormatUnavailable) throw failure;
            throw new YtDlpException(failure.Kind, failure.Message) { Formats = await ListFormatsAsync(videoId, ct) };
        }

        return await ReadDownloadAsync(workDir, videoId, ct)
            ?? throw new YtDlpException(YtDlpFailure.Other, "yt-dlp n'a produit aucun fichier audio.");
    }

    /// <summary>Fichier audio et fiche d'une vidéo déjà téléchargée dans <paramref name="workDir"/>, ou null s'il en manque.</summary>
    public static async Task<YtDownload?> ReadDownloadAsync(string workDir, string videoId, CancellationToken ct)
    {
        var infoPath = Path.Combine(workDir, $"{videoId}.info.json");
        if (!Directory.Exists(workDir) || !File.Exists(infoPath)) return null;
        var audio = Directory.EnumerateFiles(workDir, $"{videoId}.*")
            .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (audio is null) return null;
        var info = JsonNode.Parse(await File.ReadAllTextAsync(infoPath, ct))!.AsObject();
        return new YtDownload(audio, info);
    }

    private static void Clear(string workDir, string videoId)
    {
        foreach (var f in Directory.EnumerateFiles(workDir, $"{videoId}.*")) File.Delete(f);
    }

    /// <summary>Tables « --list-formats » de la vidéo : connecté puis sans cookies quand il y en a (la différence se voit).</summary>
    public async Task<string> ListFormatsAsync(string videoId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        bool[] modes = settings.CookiesPath is null ? [false] : [true, false];
        foreach (var signedIn in modes)
        {
            var run = await TryRunAsync(["--no-playlist", "--no-warnings", "--list-formats", WatchUrl(videoId)], null, ListTimeout, signedIn, ct);
            sb.AppendLine(Tx.Key(modes.Length == 1 ? "errors.yt.formatsAvailable" : signedIn ? "errors.yt.formatsSignedIn" : "errors.yt.formatsAnonymous"));
            var lines = run.Output.Replace("\r", "").Split('\n');
            var heading = Array.FindIndex(lines, l => l.StartsWith("[info] Available formats", StringComparison.Ordinal));
            sb.AppendLine(run.Failure is { } f ? Tx.Key("errors.yt.formatsFailed", ("message", f.Message))
                : heading < 0 ? Tx.Key("errors.yt.formatsNoTable")
                : string.Join('\n', lines.Skip(heading + 1)).TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private readonly record struct Run(string Output, YtDlpException? Failure, bool SignedIn);

    private async Task<string> RunAsync(List<string> args, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        var run = await TryRunAsync(args, onLine, timeout, signedIn: true, ct);
        return run.Failure is { } f ? throw f : run.Output;
    }

    /// <summary>Lance yt-dlp ; un échec d'exécution est renvoyé (pas levé) pour qu'on puisse réessayer autrement.</summary>
    /// <param name="signedIn">Avec les cookies, s'il y en a.</param>
    private async Task<Run> TryRunAsync(List<string> args, Action<string>? onLine, TimeSpan timeout, bool signedIn, CancellationToken ct)
    {
        var exe = tools.YtDlp ?? throw new YtDlpException(YtDlpFailure.Other, Tx.Key("errors.yt.missing"));
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
        var cookies = signedIn ? settings.CookiesPath : null;
        if (cookies is not null) { start.ArgumentList.Add("--cookies"); start.ArgumentList.Add(cookies); }
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new YtDlpException(YtDlpFailure.Other, Tx.Key("errors.yt.cannotStart"));
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
        if (timeoutCts.IsCancellationRequested) throw new YtDlpException(YtDlpFailure.Other, Tx.Key("errors.yt.timeout"));
        if (process.ExitCode == 0) return new Run(stdout.ToString(), null, cookies is not null);

        var error = string.Join('\n', stderr.ToString().Split('\n').Where(l => l.StartsWith("ERROR", StringComparison.Ordinal))).Trim();
        if (error.Length == 0) error = stderr.ToString().Trim();
        logger.LogWarning("yt-dlp a échoué ({Code}, {Mode}) : {Error}", process.ExitCode, cookies is null ? "sans cookies" : "connecté", error);
        var kind = Classify(error);
        var message = Shorten(error);
        if (kind == YtDlpFailure.NeedsAccount)
            message += cookies is null
                ? Tx.Key("errors.yt.addCookies")
                : Tx.Key("errors.yt.cookiesInsufficient");
        return new Run(stdout.ToString(), new YtDlpException(kind, message), cookies is not null);
    }

    internal static YtDlpFailure Classify(string error)
    {
        if (FormatMissingPattern().IsMatch(error)) return YtDlpFailure.FormatUnavailable;
        if (NeedsAccountPattern().IsMatch(error)) return YtDlpFailure.NeedsAccount;
        if (ThrottledPattern().IsMatch(error)) return YtDlpFailure.Throttled;
        if (UnavailablePattern().IsMatch(error)) return YtDlpFailure.Unavailable;
        return YtDlpFailure.Other;
    }

    [GeneratedRegex(@"Requested format is not available", RegexOptions.IgnoreCase)]
    private static partial Regex FormatMissingPattern();

    [GeneratedRegex(@"confirm your age|age.restricted|inappropriate for some users|members.only|join this channel|available to this channel's members|Sign in to view|requires? (a )?(sign.in|login|account)", RegexOptions.IgnoreCase)]
    private static partial Regex NeedsAccountPattern();

    [GeneratedRegex(@"HTTP Error 429|Too Many Requests|confirm you.re not a bot|rate.limit|try again later", RegexOptions.IgnoreCase)]
    private static partial Regex ThrottledPattern();

    [GeneratedRegex(@"Video unavailable|Private video|has been removed|account associated with this video has been terminated|not available in your country|This video is unavailable|copyright", RegexOptions.IgnoreCase)]
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
