using System.Globalization;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Filtres de la revue des propositions (status : pending, accepted, rejected, superseded ou all ; score de 0 à 1).</summary>
public sealed record ProposalQuery(string Status = "pending", Guid? SuiteId = null, string? Artist = null, double? MinScore = null, double? MaxScore = null)
{
    public string ToQueryString()
    {
        var q = $"&status={Uri.EscapeDataString(Status)}";
        if (SuiteId is { } suite) q += $"&suiteId={suite}";
        if (!string.IsNullOrWhiteSpace(Artist)) q += $"&artist={Uri.EscapeDataString(Artist.Trim())}";
        if (MinScore is { } min) q += "&minScore=" + min.ToString(CultureInfo.InvariantCulture);
        if (MaxScore is { } max) q += "&maxScore=" + max.ToString(CultureInfo.InvariantCulture);
        return q;
    }
}

/// <summary>Ce que les pages de revue (propositions, doublons) sélectionnent et envoient.</summary>
public static class ReviewSelection
{
    /// <summary>Propositions tranchées par envoi (limite du serveur).</summary>
    public const int BatchSize = 500;

    /// <summary>Propositions dont le score atteint <paramref name="min"/>.</summary>
    public static IEnumerable<Guid> AtLeast(IEnumerable<RescanProposalDto> items, double min) =>
        items.Where(p => p.Score >= min).Select(p => p.Id);

    public static IEnumerable<IReadOnlyList<Guid>> Batches(IReadOnlyCollection<Guid> ids) => ids.Chunk(BatchSize);

    /// <summary>Copies écartées si l'on garde <paramref name="keepId"/> : supprimées (uploads/) ou seulement masquées (lecture seule).</summary>
    public static DuplicateRemoval Removal(IReadOnlyList<DuplicateCopyDto> copies, Guid keepId)
    {
        var removed = copies.Where(c => c.TrackId != keepId).ToList();
        return new DuplicateRemoval(removed.Where(c => c.InUploads).ToList(), removed.Where(c => !c.InUploads).ToList(), removed.Select(c => c.TrackId).ToList());
    }
}

public sealed record DuplicateRemoval(IReadOnlyList<DuplicateCopyDto> Deleted, IReadOnlyList<DuplicateCopyDto> Hidden, IReadOnlyList<Guid> RemoveIds);
