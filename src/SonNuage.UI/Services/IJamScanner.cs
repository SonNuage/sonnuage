namespace SonNuage.UI.Services;

/// <summary>Scan d'un QR code de Jam par la caméra de l'appareil. L'hôte capable le fournit ; sinon on colle le lien.</summary>
public interface IJamScanner
{
    /// <summary>Cet appareil sait scanner un QR code.</summary>
    bool Available { get; }

    /// <summary>Scanne un QR code et renvoie son texte (le lien), ou null si annulé.</summary>
    Task<string?> ScanAsync(CancellationToken ct);
}

/// <summary>Par défaut : pas de scan (web, ou appareil sans caméra exposée) ; on colle le lien à la main.</summary>
public sealed class NoJamScanner : IJamScanner
{
    public bool Available => false;
    public Task<string?> ScanAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
