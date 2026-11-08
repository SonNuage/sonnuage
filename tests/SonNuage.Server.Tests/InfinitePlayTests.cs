using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Lecture infinie : garder toujours « avance » morceaux après celui qui joue.</summary>
public sealed class InfinitePlayTests
{
    [Theory]
    // 5 morceaux, avance 3 : rien à ajouter aux deux premiers, 1 au 3e (le 6e), 2 au 4e…
    [InlineData(5, 0, 3, 0)]
    [InlineData(5, 1, 3, 0)]
    [InlineData(5, 2, 3, 1)]
    [InlineData(6, 3, 3, 1)]
    [InlineData(5, 4, 3, 3)]
    [InlineData(1, 0, 5, 5)]
    // Rien qui joue : on n'invente pas de file.
    [InlineData(0, -1, 3, 0)]
    public void Keeps_the_chosen_number_of_tracks_ahead(int count, int index, int ahead, int expected) =>
        Assert.Equal(expected, InfinitePlay.Needed(count, index, ahead));

    [Fact]
    public void Seeds_are_the_last_tracks_up_to_the_current_one()
    {
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(ids[2..6], InfinitePlay.Seeds(ids, currentIndex: 5));
        Assert.Equal(ids[..2], InfinitePlay.Seeds(ids, currentIndex: 1));
    }
}
