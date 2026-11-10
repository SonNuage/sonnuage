using System.Globalization;
using SonNuage.Server.Library;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Discovery;

/// <summary>Type de parution retenu pour les nouveautés (groupe de parutions MusicBrainz).</summary>
public enum ReleaseKind
{
    Album,
    Ep,
    Single,
}

/// <summary>Place d'une parution par rapport à aujourd'hui : sortie récente ou à venir.</summary>
public enum ReleaseWindow
{
    Recent,
    Upcoming,
}

/// <summary>Album de la bibliothèque, tel que le voit la reconnaissance des parutions (titre normalisé, groupe MusicBrainz).</summary>
public sealed record LibraryAlbumKey(Guid AlbumId, string NormalizedTitle, Guid? ReleaseGroupId);

/// <summary>Règles des nouveautés des artistes de la bibliothèque (couche 2 de la découverte), sans accès aux données.</summary>
public static class ReleaseRules
{
    /// <summary>Une parution est « récente » pendant ce nombre de jours.</summary>
    public const int RecentDays = 90;

    /// <summary>« Various Artists » : ses parutions (des milliers de compilations) ne sont jamais lues.</summary>
    public const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

    /// <summary>Types secondaires écartés : ce ne sont pas de nouvelles œuvres de l'artiste.</summary>
    private static readonly HashSet<string> IgnoredSecondaryTypes = new(StringComparer.OrdinalIgnoreCase) { "Compilation", "Live", "Remix", "DJ-mix" };

    /// <summary>Album, EP ou single ; null pour les autres types et pour les compilations, live et remix.</summary>
    public static ReleaseKind? KindOf(string? primaryType, IEnumerable<string> secondaryTypes)
    {
        if (secondaryTypes.Any(IgnoredSecondaryTypes.Contains)) return null;
        return primaryType?.ToLowerInvariant() switch
        {
            "album" => ReleaseKind.Album,
            "ep" => ReleaseKind.Ep,
            "single" => ReleaseKind.Single,
            _ => null,
        };
    }

    /// <summary>Date MusicBrainz, éventuellement partielle (« 2026 », « 2026-11 ») : le premier jour de la période.</summary>
    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var format in new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" })
        {
            if (DateOnly.TryParseExact(value.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        }
        return null;
    }

    /// <summary>Récente (jusqu'à <see cref="RecentDays"/> jours, aujourd'hui compris), à venir (après aujourd'hui), ou ni l'une ni l'autre.</summary>
    public static ReleaseWindow? WindowOf(DateOnly? date, DateOnly today) => date switch
    {
        null => null,
        { } d when d > today => ReleaseWindow.Upcoming,
        { } d when d >= today.AddDays(-RecentDays) => ReleaseWindow.Recent,
        _ => null,
    };

    /// <summary>
    /// Album de la bibliothèque qui correspond à la parution : même groupe MusicBrainz, sinon même titre normalisé
    /// (casse, accents et suffixe entre parenthèses ou crochets ignorés : « Discovery (Remastered) » est « Discovery »).
    /// </summary>
    public static Guid? MatchAlbum(Guid releaseGroupId, string title, IEnumerable<LibraryAlbumKey> albums)
    {
        var list = albums as IReadOnlyCollection<LibraryAlbumKey> ?? albums.ToList();
        if (list.FirstOrDefault(a => a.ReleaseGroupId == releaseGroupId) is { } byGroup) return byGroup.AlbumId;
        var key = NameNormalizer.Key(title);
        return list.FirstOrDefault(a => a.NormalizedTitle == key)?.AlbumId;
    }

    /// <summary>
    /// Échéance du cache des parutions d'un artiste : 3 jours pour un artiste écouté ces derniers temps, 7 sinon, plus
    /// jusqu'à un jour selon l'identifiant, pour que les relectures s'étalent au lieu de tomber ensemble.
    /// </summary>
    public static DateTimeOffset ExpiresAt(DateTimeOffset now, bool listened, string artistMbid)
    {
        var hash = 17;
        foreach (var c in artistMbid) hash = unchecked(hash * 31 + c);
        var spread = TimeSpan.FromMinutes((hash & int.MaxValue) % (24 * 60));
        return now + TimeSpan.FromDays(listened ? 3 : 7) + spread;
    }
}

/// <summary>Déduit l'identifiant MusicBrainz d'un artiste des crédits de ses albums ou morceaux identifiés.</summary>
public static class ArtistMbidDeriver
{
    /// <summary>
    /// Identifiant de l'artiste crédité sous ce nom (nom crédité ou nom MusicBrainz, casse et accents ignorés) dans
    /// les crédits lus ; null si aucun ne le crédite ou si deux identifiants différents se présentent.
    /// </summary>
    public static Guid? Pick(string artistName, IEnumerable<IReadOnlyList<MbArtistCredit>> credits)
    {
        var key = NameNormalizer.Key(artistName);
        var ids = credits.SelectMany(c => c)
            .Where(c => NameNormalizer.Key(c.CreditedName) == key || NameNormalizer.Key(c.ArtistName) == key)
            .Select(c => c.Id)
            .Distinct()
            .ToList();
        return ids.Count == 1 ? ids[0] : null;
    }
}
