namespace SonNuage.Contracts;

/// <summary>
/// Sauvegarde automatique de la base : chaque jour à At (heure locale du fuseau TimeZone, identifiant IANA), une copie
/// à chaud ; les Keep plus récentes sont gardées.
/// </summary>
public sealed record BackupSettingsDto(bool Enabled, TimeOnly At, int Keep, string TimeZone)
{
    public const int MaxKeep = 60;
    public static readonly BackupSettingsDto Defaults = new(true, new TimeOnly(4, 0), 7, "Europe/Paris");
}

public sealed record BackupFileDto(string Name, long Bytes, DateTimeOffset CreatedAt);

/// <summary>Réglages, dossier des sauvegardes (côté serveur) et sauvegardes présentes, la plus récente en tête.</summary>
public sealed record BackupStatusDto(BackupSettingsDto Settings, string Folder, IReadOnlyList<BackupFileDto> Files);
