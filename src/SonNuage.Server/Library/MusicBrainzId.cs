using System.Text.RegularExpressions;

namespace SonNuage.Server.Library;

/// <summary>
/// Identifiants MusicBrainz venus de l'extérieur (tags, saisie de l'admin, réponses MusicBrainz) : lus une fois, ici.
/// Accepte l'identifiant seul, une liste (« id1/id2 », certains tags en portent plusieurs : le premier compte) ou un lien
/// <c>https://musicbrainz.org/release/…</c>.
/// </summary>
public static partial class MusicBrainzId
{
    public static Guid? Parse(string? value) =>
        value is not null && Uuid().Match(value) is { Success: true } m && Guid.TryParse(m.Value, out var id) && id != Guid.Empty ? id : null;

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();
}
