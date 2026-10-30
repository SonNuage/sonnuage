using SonNuage.Contracts.Localization;
using System.Collections.Concurrent;
using SonNuage.Contracts;
using SonNuage.Server.Library;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Admin;

/// <summary>
/// Upload découpé et reprenable : le client envoie des morceaux de <see cref="ChunkSize"/> octets à l'offset courant.
/// Chaque requête reste bien sous la limite de 100 Mo de Cloudflare, et une coupure réseau ne fait perdre qu'un morceau.
/// </summary>
public sealed class UploadService(IOptions<SonNuageOptions> options, MetadataReader metadata, ILogger<UploadService> logger)
{
    public const int ChunkSize = 8 * 1024 * 1024;
    public const long MaxFileSize = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<Guid, UploadSession> _sessions = new();

    public (UploadSessionDto? Session, string? Error) Start(StartUploadRequest req)
    {
        Cleanup();
        var name = SafeFileName(req.FileName);
        if (!LibraryFiles.IsAudio(name)) return (null, Tx.Key("errors.upload.unknownFormat"));
        if (req.Size is <= 0 or > MaxFileSize) return (null, Tx.Key("errors.upload.badSize"));

        Directory.CreateDirectory(options.Value.UploadTempPath);
        var session = new UploadSession(Guid.NewGuid(), name, req.Size, req.ArtistHint, req.AlbumHint,
            Path.Combine(options.Value.UploadTempPath, $"{Guid.NewGuid():N}{Path.GetExtension(name)}"));
        File.Create(session.TempPath).Dispose();
        _sessions[session.Id] = session;
        return (session.ToDto(), null);
    }

    public UploadSessionDto? Get(Guid id) => _sessions.TryGetValue(id, out var s) ? s.ToDto() : null;

    public async Task<(UploadSessionDto? Session, string? Error)> AppendAsync(Guid id, long offset, Stream body, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s)) return (null, Tx.Key("errors.upload.unknownSession"));
        await s.Lock.WaitAsync(ct);
        try
        {
            if (offset != s.Received) return (s.ToDto(), Tx.Key("errors.upload.offset", ("offset", s.Received)));

            await using var file = new FileStream(s.TempPath, FileMode.Open, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            file.Seek(s.Received, SeekOrigin.Begin);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > ChunkSize || s.Received + written > s.Size)
                {
                    file.SetLength(s.Received);
                    return (s.ToDto(), Tx.Key("errors.upload.chunkTooLarge"));
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            s.Received += written;
            s.Touched = DateTimeOffset.UtcNow;
            return (s.ToDto(), null);
        }
        finally
        {
            s.Lock.Release();
        }
    }

    /// <summary>Range le fichier complet dans <c>Uploads/Artiste/Album/</c> d'après ses tags (ou les indications du formulaire).</summary>
    public (string? Path, string? Error) Complete(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return (null, Tx.Key("errors.upload.unknownSession"));
        if (s.Received != s.Size) return (null, Tx.Key("errors.upload.incomplete", ("received", s.Received), ("size", s.Size)));

        var meta = metadata.Read(s.TempPath, options.Value.UploadTempPath);
        var artist = FirstReal(meta.AlbumArtist, s.ArtistHint) ?? "Artiste inconnu";
        var album = FirstReal(meta.Album, s.AlbumHint) ?? "Album inconnu";

        var dir = Path.Combine(Path.GetFullPath(options.Value.UploadPath), SafeSegment(artist), SafeSegment(album));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, s.FileName);
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(s.FileName)} ({i}){Path.GetExtension(s.FileName)}");

        File.Move(s.TempPath, target);
        _sessions.TryRemove(id, out _);
        logger.LogInformation("Upload terminé : {Target}", target);
        return (target, null);

        // Les tags « inconnus » produits par le lecteur ne doivent pas l'emporter sur ce que l'admin a saisi.
        static string? FirstReal(string? fromTags, string? hint) =>
            !string.IsNullOrWhiteSpace(hint) && (fromTags is null or "Artiste inconnu" or "Album inconnu")
                ? hint.Trim()
                : string.IsNullOrWhiteSpace(fromTags) ? hint?.Trim() : fromTags;
    }

    public void Cancel(Guid id)
    {
        if (_sessions.TryRemove(id, out var s)) TryDelete(s.TempPath);
    }

    private void Cleanup()
    {
        foreach (var (id, s) in _sessions)
        {
            if (DateTimeOffset.UtcNow - s.Touched > SessionLifetime) Cancel(id);
        }
    }

    internal static string SafeFileName(string name) => SafeSegment(Path.GetFileName(name.Replace('\\', '/')));

    /// <summary>Nom de dossier ou de fichier sûr : « AC/DC » devient « AC_DC » (et non « DC »).</summary>
    internal static string SafeSegment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (cleaned.Length > 180) cleaned = cleaned[..180];
        return cleaned.Length == 0 ? "sans-nom" : cleaned;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }

    private sealed class UploadSession(Guid id, string fileName, long size, string? artistHint, string? albumHint, string tempPath)
    {
        public Guid Id { get; } = id;
        public string FileName { get; } = fileName;
        public long Size { get; } = size;
        public string? ArtistHint { get; } = artistHint;
        public string? AlbumHint { get; } = albumHint;
        public string TempPath { get; } = tempPath;
        public long Received { get; set; }
        public DateTimeOffset Touched { get; set; } = DateTimeOffset.UtcNow;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public UploadSessionDto ToDto() => new(Id, FileName, Size, Received, ChunkSize);
    }
}
