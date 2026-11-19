using System.Text.Json;

namespace SonNuage.Server.Jam;

/// <summary>
/// Message <c>social-connect/v2/session_update</c> du dealer : la session du Jam a changé (membre arrivé ou parti,
/// pause…). Certaines raisons veulent dire que le Jam est fini pour nous ; les autres ne changent rien au suivi.
/// </summary>
public static class SessionUpdate
{
    public const string Uri = "social-connect/v2/session_update";

    /// <summary>Raisons (énumération de l'appli Spotify) après lesquelles il n'y a plus rien à suivre.</summary>
    private static readonly HashSet<string> Ending =
        ["SESSION_DELETED", "YOU_LEFT", "YOU_WERE_KICKED", "YOU_WERE_REMOVED_UNSUPPORTED_CONTENT"];

    /// <summary>Raison de la mise à jour (champ <c>reason</c>, où qu'il soit), ou null si illisible.</summary>
    public static string? Reason(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return FindReason(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool EndsJam(byte[] payload) => Reason(payload) is { } reason && Ending.Contains(reason);

    private static string? FindReason(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            if (p.NameEquals("reason") && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            if (FindReason(p.Value) is { } nested) return nested;
        }
        return null;
    }
}
