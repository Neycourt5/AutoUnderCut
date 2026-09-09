using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// The regression these cover was observed live on 1.0.0.61: the scout window
/// rotated across the whole rule list, so by the third world of a circuit it had
/// walked past every gemdraught and spent the stop pricing 200-gil materia.
/// </summary>
public sealed class ShoppingScoutPolicyTests
{
    private static readonly uint[] Food = [1, 2, 3, 4, 5, 6];
    private static readonly uint[] Secondary = Enumerable.Range(100, 46).Select(x => (uint)x).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(30)]
    public void EveryWorldPricesTheWholeFoodAndPotionBlock(int worldIndex)
    {
        var selected = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [], [], worldIndex, 12);

        Assert.All(Food, item => Assert.Contains(item, selected));
    }

    [Fact]
    public void SecondaryLinesStillRotateSoEachGetsATurn()
    {
        var first = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [], [], 0, 12);
        var later = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [], [], 3, 12);

        // The block is identical on both stops; what rotates is everything else.
        Assert.Equal(Food, first.Intersect(Food));
        Assert.Equal(Food, later.Intersect(Food));
        Assert.NotEqual(first.Except(Food), later.Except(Food));
    }

    [Fact]
    public void TheBlockIsKeptWhenTheLimitCannotHoldEverything()
    {
        // A tight limit spends itself on the food rather than the rotation.
        var selected = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [], [], 5, 6);

        Assert.Equal(Food, selected);
    }

    [Fact]
    public void AResumedItemIsNeverLostToTheBlock()
    {
        var selected = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [777u], [], 2, 7);

        Assert.Contains(777u, selected);
        Assert.All(Food, item => Assert.Contains(item, selected));
    }

    [Fact]
    public void HintedBargainsFillTheRemainderAheadOfTheRotation()
    {
        var selected = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [], [140u, 141u], 0, 8);

        Assert.Contains(140u, selected);
        Assert.Contains(141u, selected);
        Assert.Equal(8, selected.Count);
    }

    [Fact]
    public void NoSecondaryLinesIsNotADivideByZero()
    {
        Assert.Equal(Food, ShoppingScoutPolicy.SelectWorldItems(Food, [], [], [], 4, 12));
    }

    [Fact]
    public void ItemsAreNeverPricedTwiceOnOneWorld()
    {
        // The resumed item is also a food line and a hint; it must cost one search.
        var selected = ShoppingScoutPolicy.SelectWorldItems(Food, Secondary, [3u], [3u, 100u], 1, 12);

        Assert.Equal(selected.Count, selected.Distinct().Count());
    }
}
