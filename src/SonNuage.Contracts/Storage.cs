namespace SonNuage.Contracts;

/// <summary>
/// Garde-fous disque réglés par l'admin. <paramref name="MinFreeGb"/> : en dessous de cet espace libre sur le disque
/// de la musique, plus aucun téléchargement (imports YouTube, demandes, extraits) jusqu'à ce que la place revienne.
/// <paramref name="PreviewQuotaGb"/> : place allouée aux extraits ; au-delà, les plus anciens sont supprimés.
/// </summary>
public sealed record StorageSettingsDto(double MinFreeGb, double PreviewQuotaGb)
{
    public static readonly StorageSettingsDto Defaults = new(5, 2);
    public const double MaxMinFreeGb = 100_000;
    public const double MinPreviewQuotaGb = 0.1;
    public const double MaxPreviewQuotaGb = 1_000;
}

/// <param name="FreeBytes">Espace libre du disque de la musique (null s'il est illisible).</param>
/// <param name="PreviewBytes">Place occupée par les extraits.</param>
public sealed record StorageStatusDto(StorageSettingsDto Settings, long? FreeBytes, long? TotalBytes, long PreviewBytes, bool DownloadsBlocked);
