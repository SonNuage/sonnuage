using System.Text.Json;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;

namespace SonNuage.Server.Lyrics;

/// <summary>Réponse de LRCLIB gardée sur disque. Sans texte : rien de trouvé à cette date.</summary>
internal sealed record CachedLyrics(string? Synced, string? Plain, DateTimeOffset FetchedAt)
{
    public bool IsEmpty => Synced is null && Plain is null;
}

/// <summary>
/// Paroles d'un morceau : le fichier .lrc posé à côté du fichier audio d'abord, sinon LRCLIB.
/// Les réponses de LRCLIB sont gardées dans <c>&lt;DataPath&gt;/lyrics/&lt;id&gt;.json</c>, une absence aussi, redemandée
/// après une semaine. Un LRCLIB injoignable n'est pas une absence : on redemandera la fois suivante.
/// </summary>
public sealed class LyricsService(LrclibClient lrclib, IOptions<SonNuageOptions> options, TimeProvider time, ILogger<LyricsService> logger)
{
    public const string LocalSource = "local";
    public const string LrclibSource = "lrclib";
    /// <summary>Délai avant de redemander des paroles introuvables.</summary>
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Paroles du morceau, null s'il n'y en a nulle part (ou que LRCLIB ne répond pas).</summary>
    public async Task<LyricsDto?> FindAsync(Guid trackId, string audioPath, LyricsQuery query, CancellationToken ct)
    {
        if (await LocalAsync(audioPath, ct) is { Lines.Count: > 0 } local) return local;

        var path = Path.Combine(options.Value.LyricsCachePath, $"{trackId:N}.json");
        var cached = await ReadAsync(path, ct);
        if (cached is null || cached.IsEmpty && cached.FetchedAt + RetryMissingAfter <= time.GetUtcNow())
        {
            var fetched = await FetchAsync(query, ct);
            if (fetched is null) return ToDto(cached);
            cached = new CachedLyrics(fetched.Synced, fetched.Plain, time.GetUtcNow());
            await WriteAsync(path, cached, ct);
        }
        return ToDto(cached);
    }

    private static LyricsDto? ToDto(CachedLyrics? cached)
    {
        if (cached?.Synced is { } synced && LrcParser.Parse(synced, LrclibSource) is { Synced: true } timed) return timed;
        return cached?.Plain is { } plain && LrcParser.Plain(plain, LrclibSource) is { Lines.Count: > 0 } text ? text : null;
    }

    private static async Task<LyricsDto?> LocalAsync(string audioPath, CancellationToken ct)
    {
        var lrc = Path.ChangeExtension(audioPath, ".lrc");
        if (!File.Exists(lrc)) return null;
        try
        {
            return LrcParser.Parse(await File.ReadAllTextAsync(lrc, ct), LocalSource);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<LrclibLyrics?> FetchAsync(LyricsQuery query, CancellationToken ct)
    {
        try
        {
            return await lrclib.FindAsync(query, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Paroles de « {Title} » indisponibles sur LRCLIB : {Error}", query.Title, ex.Message);
            return null;
        }
    }

    private static async Task<CachedLyrics?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CachedLyrics>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    // Écrit à côté puis remplace : deux demandes simultanées ne laissent jamais un fichier à moitié écrit.
    private static async Task WriteAsync(string path, CachedLyrics cached, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cached, Json), ct);
        File.Move(temp, path, overwrite: true);
    }
}
