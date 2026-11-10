using System.Globalization;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Logique des pages de revue de l'admin (propositions des rescans, doublons), hors affichage.</summary>
public sealed class LibraryReviewUiTests
{
    private static RescanProposalDto Proposal(double score) => new(Guid.NewGuid(), Guid.NewGuid(), null, null, "Pending", score,
        "t", "a", "al", "T", "A", null, null, null, [], false, false, DateTimeOffset.UtcNow, null, null);

    private static DuplicateCopyDto Copy(bool inUploads) => new(Guid.NewGuid(), "t", "a", "al", "p", "flac", null, null, true, 1, 1, inUploads, 0, 0, 0, 0);

    [Fact]
    public void Filters_become_an_invariant_query_string()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var suite = Guid.NewGuid();
            Assert.Equal($"&status=pending&suiteId={suite}&artist=Les%20Inconnus&minScore=0.85&maxScore=0.9",
                new ProposalQuery("pending", suite, " Les Inconnus ", 0.85, 0.9).ToQueryString());
            Assert.Equal("&status=all", new ProposalQuery("all").ToQueryString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Selecting_by_score_keeps_the_proposals_at_or_above_it()
    {
        var items = new[] { Proposal(0.95), Proposal(0.9), Proposal(0.6) };
        Assert.Equal(new[] { items[0].Id, items[1].Id }.Order(), ReviewSelection.AtLeast(items, 0.9).Order());
        Assert.Empty(ReviewSelection.AtLeast(items, 0.99));
    }

    [Fact]
    public void A_large_selection_is_sent_in_batches_the_server_accepts()
    {
        var ids = Enumerable.Range(0, 1201).Select(_ => Guid.NewGuid()).ToList();
        var batches = ReviewSelection.Batches(ids).ToList();
        Assert.Equal([500, 500, 201], batches.Select(b => b.Count));
        Assert.Equal(ids, batches.SelectMany(b => b));
    }

    [Fact]
    public void The_confirmation_lists_which_copies_are_deleted_and_which_are_only_hidden()
    {
        var kept = Copy(inUploads: true);
        var uploaded = Copy(inUploads: true);
        var readOnly = Copy(inUploads: false);
        var plan = ReviewSelection.Removal([kept, uploaded, readOnly], kept.TrackId);
        Assert.Equal([uploaded], plan.Deleted);
        Assert.Equal([readOnly], plan.Hidden);
        Assert.Equal([uploaded.TrackId, readOnly.TrackId], plan.RemoveIds);
    }
}
