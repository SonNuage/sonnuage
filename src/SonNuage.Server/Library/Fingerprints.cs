using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Library;

/// <summary>Empreinte Chromaprint brute d'un fichier : durée analysée et entiers 32 bits (environ 8 par seconde).</summary>
public sealed record RawFingerprint(double DurationSeconds, uint[] Values);

/// <summary>Calcule l'empreinte acoustique d'un fichier (<see cref="FpcalcFingerprinter"/> en production, un faux dans les tests).</summary>
public interface IFingerprinter
{
    /// <summary>L'outil est installé. Ne démarre rien.</summary>
    bool Available { get; }

    /// <exception cref="IOException">Fichier illisible ou outil en échec.</exception>
    Task<RawFingerprint> ComputeAsync(string path, CancellationToken ct);
}

/// <summary>fpcalc (Chromaprint), en local : aucun appel à AcoustID, donc aucune limite de débit.</summary>
public sealed class FpcalcFingerprinter(YoutubeTools tools) : IFingerprinter
{
    /// <summary>Secondes analysées depuis le début du fichier (réglage par défaut de fpcalc).</summary>
    private const int Length = 120;

    public bool Available => tools.Fpcalc is not null;

    public async Task<RawFingerprint> ComputeAsync(string path, CancellationToken ct)
    {
        var fpcalc = tools.Fpcalc ?? throw new IOException(Tx.Key("errors.jobs.noFpcalc"));
        var start = new ProcessStartInfo(fpcalc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-raw", "-json", "-length", Length.ToString(), path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("fpcalc");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new IOException((await error).Trim() is { Length: > 0 } e ? e : $"fpcalc : code {process.ExitCode}");
        try
        {
            using var json = JsonDocument.Parse(await output);
            var root = json.RootElement;
            // Valeurs non signées par défaut ; signées avec certaines versions : même motif de bits.
            var values = root.GetProperty("fingerprint").EnumerateArray().Take(Fingerprints.MaxValues).Select(v => unchecked((uint)v.GetInt64())).ToArray();
            return new RawFingerprint(root.GetProperty("duration").GetDouble(), values);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}

/// <summary>Stockage et comparaison des empreintes brutes.</summary>
public static class Fingerprints
{
    /// <summary>Valeurs gardées par morceau (environ 32 premières secondes) : assez pour reconnaître un enregistrement.</summary>
    public const int MaxValues = 256;
    /// <summary>Recouvrement minimal pour conclure (environ une seconde).</summary>
    private const int MinOverlap = 8;
    /// <summary>Décalage toléré, en trames (délai d'encodeur, silence au début).</summary>
    private const int MaxOffset = 2;

    public static byte[] ToBytes(uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    public static uint[] FromBytes(byte[] bytes)
    {
        var values = new uint[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        return values;
    }

    /// <summary>
    /// Proportion de bits identiques (1 = même empreinte, 0,5 environ pour deux morceaux sans rapport), au meilleur
    /// alignement à quelques trames près.
    /// </summary>
    public static double Similarity(uint[] a, uint[] b)
    {
        var best = 0.0;
        for (var offset = -MaxOffset; offset <= MaxOffset; offset++)
        {
            var ia = Math.Max(0, offset);
            var ib = Math.Max(0, -offset);
            var n = Math.Min(a.Length - ia, b.Length - ib);
            if (n < MinOverlap) continue;
            var errors = 0;
            for (var i = 0; i < n; i++) errors += BitOperations.PopCount(a[ia + i] ^ b[ib + i]);
            best = Math.Max(best, 1 - errors / (32.0 * n));
        }
        return best;
    }
}
