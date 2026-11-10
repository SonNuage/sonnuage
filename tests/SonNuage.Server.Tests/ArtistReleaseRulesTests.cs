using SonNuage.Server.Discovery;
using SonNuage.Server.Library;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Règles des nouveautés des artistes : types retenus, dates, fenêtre, reconnaissance dans la bibliothèque.</summary>
public sealed class ArtistReleaseRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    [Theory]
    [InlineData("Album", "", ReleaseKind.Album)]
    [InlineData("EP", "", ReleaseKind.Ep)]
    [InlineData("single", "", ReleaseKind.Single)]
    [InlineData("Album", "Soundtrack", ReleaseKind.Album)]
    [InlineData("Album", "Compilation", null)]
    [InlineData("Album", "Live", null)]
    [InlineData("Single", "Remix", null)]
    [InlineData("Album", "DJ-mix", null)]
    [InlineData("Broadcast", "", null)]
    [InlineData("Other", "", null)]
    [InlineData(null, "", null)]
    public void Compilations_live_and_remixes_are_left_out(string? primary, string secondary, ReleaseKind? expected) =>
        Assert.Equal(expected, ReleaseRules.KindOf(primary, secondary.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    [InlineData("2026-11-20", "2026-11-20")]
    [InlineData("2026-11", "2026-11-01")]
    [InlineData("2026", "2026-01-01")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("bientôt", null)]
    [InlineData("2026-13-01", null)]
    public void Partial_dates_start_at_their_first_day(string? value, string? expected) =>
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), ReleaseRules.ParseDate(value));

    [Theory]
    [InlineData("2026-11-10", ReleaseWindow.Recent)]
    [InlineData("2026-08-12", ReleaseWindow.Recent)]
    [InlineData("2026-08-11", null)]
    [InlineData("2026-11-11", ReleaseWindow.Upcoming)]
    [InlineData("2027-03-01", ReleaseWindow.Upcoming)]
    [InlineData(null, null)]
    public void Recent_means_the_last_ninety_days_and_upcoming_anything_after_today(string? date, ReleaseWindow? expected) =>
        Assert.Equal(expected, ReleaseRules.WindowOf(date is null ? null : DateOnly.Parse(date), Today));

    [Fact]
    public void A_release_is_in_the_library_by_release_group_or_by_title()
    {
        Guid group = Guid.NewGuid(), byGroup = Guid.NewGuid(), byTitle = Guid.NewGuid(), deluxe = Guid.NewGuid();
        var albums = new[]
        {
            new LibraryAlbumKey(byGroup, NameNormalizer.Key("Renamed by hand"), group),
            new LibraryAlbumKey(byTitle, NameNormalizer.Key("Discovery"), null),
            new LibraryAlbumKey(deluxe, NameNormalizer.Key("Random Access Memories [Deluxe Edition]"), null),
        };

        Assert.Equal(byGroup, ReleaseRules.MatchAlbum(group, "Whatever", albums));
        Assert.Equal(byTitle, ReleaseRules.MatchAlbum(Guid.NewGuid(), "Discovery", albums));
        Assert.Equal(byTitle, ReleaseRules.MatchAlbum(Guid.NewGuid(), "Discovery (Remastered)", albums));
        Assert.Equal(deluxe, ReleaseRules.MatchAlbum(Guid.NewGuid(), "Random Access Memories", albums));
        Assert.Null(ReleaseRules.MatchAlbum(Guid.NewGuid(), "Homework", albums));
    }

    [Fact]
    public void Listened_artists_are_refreshed_more_often_and_expiries_are_spread()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var listened = ReleaseRules.ExpiresAt(now, listened: true, "5b11f4ce-a62d-471e-81fc-a69a8278c7da");
        var quiet = ReleaseRules.ExpiresAt(now, listened: false, "5b11f4ce-a62d-471e-81fc-a69a8278c7da");

        Assert.InRange(listened - now, TimeSpan.FromDays(3), TimeSpan.FromDays(4));
        Assert.InRange(quiet - now, TimeSpan.FromDays(7), TimeSpan.FromDays(8));
        Assert.Equal(listened, ReleaseRules.ExpiresAt(now, listened: true, "5b11f4ce-a62d-471e-81fc-a69a8278c7da"));
        var spread = Enumerable.Range(0, 20).Select(_ => ReleaseRules.ExpiresAt(now, true, Guid.NewGuid().ToString())).Distinct().Count();
        Assert.True(spread > 5, $"{spread} échéances distinctes");
    }

    [Fact]
    public void The_artist_id_is_derived_only_when_every_credit_agrees()
    {
        Guid daftPunk = Guid.NewGuid(), other = Guid.NewGuid(), pharrell = Guid.NewGuid();
        IReadOnlyList<MbArtistCredit> Credit(params (Guid Id, string Name)[] c) => c.Select(x => new MbArtistCredit(x.Id, x.Name, x.Name)).ToList();

        Assert.Equal(daftPunk, ArtistMbidDeriver.Pick("Daft Punk", [Credit((daftPunk, "Daft Punk"), (pharrell, "Pharrell Williams")), Credit((daftPunk, "DAFT PUNK"))]));
        // Crédité sous un autre nom : le nom de l'artiste MusicBrainz compte aussi.
        Assert.Equal(daftPunk, ArtistMbidDeriver.Pick("Daft Punk", [[new MbArtistCredit(daftPunk, "Daft Punk", "Thomas & Guy-Man")]]));
        Assert.Null(ArtistMbidDeriver.Pick("Daft Punk", [Credit((daftPunk, "Daft Punk")), Credit((other, "Daft Punk"))]));
        Assert.Null(ArtistMbidDeriver.Pick("Daft Punk", [Credit((pharrell, "Pharrell Williams"))]));
        Assert.Null(ArtistMbidDeriver.Pick("Daft Punk", []));
    }
}
