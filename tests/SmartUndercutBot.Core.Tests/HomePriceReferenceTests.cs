using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class HomePriceReferenceTests
{
    private static ProcurementMarketListing At(uint price, ulong id = 0) =>
        new(1, id == 0 ? price : id, 99, "Siren", 1, price, 5, false);

    [Fact]
    public void AWildlyUnderpricedListingDoesNotBecomeTheReference()
    {
        // One listing at a tenth of the going rate should not decide what every
        // away-world deal is measured against.
        var summary = HomePriceReference.Summarize(1, "Dye", false,
            [At(700), At(6_900), At(7_000), At(7_100), At(7_200)]);

        Assert.Equal(5, summary.Listings);
        Assert.Equal(700u, summary.Lowest);
        Assert.Equal(7_000u, summary.Median);
        Assert.Equal(6_900u, summary.Reference);
        Assert.Equal(1, summary.Ignored);
    }

    [Fact]
    public void AnOrdinaryUndercutIsKept()
    {
        // Half the median is the line; a normal undercut sits well above it.
        var summary = HomePriceReference.Summarize(1, "Dye", false,
            [At(6_500), At(6_900), At(7_000), At(7_100)]);
        Assert.Equal(6_500u, summary.Reference);
        Assert.Equal(0, summary.Ignored);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TooFewListingsToJudgeKeepsThemAll(int count)
    {
        var listings = Enumerable.Range(0, count).Select(i => At((uint)(100 + i * 10_000), (ulong)(i + 1))).ToArray();
        var summary = HomePriceReference.Summarize(1, "Dye", false, listings);
        Assert.Equal(0, summary.Ignored);
        Assert.Equal(100u, summary.Reference);
    }

    [Fact]
    public void AUniformlyCheapBoardIsNotDiscardedEntirely()
    {
        // If everything is under the floor then the median was the outlier, not
        // the board. Returning nothing would leave no reference at all.
        var summary = HomePriceReference.Summarize(1, "Dye", false, [At(10), At(11), At(12)]);
        Assert.Equal(3, summary.Listings);
        Assert.Equal(0, summary.Ignored);
        Assert.Equal(10u, summary.Reference);
    }

    [Fact]
    public void OtherItemsAndQualitiesAreNotCounted()
    {
        var summary = HomePriceReference.Summarize(1, "Dye", false,
        [
            At(7_000),
            new(2, 50, 99, "Siren", 1, 10, 5, false),      // another item
            new(1, 51, 99, "Siren", 1, 10, 5, true),        // high quality
            new(1, 52, 99, "Siren", 1, 0, 5, false),        // no price
        ]);
        Assert.Equal(1, summary.Listings);
        Assert.Equal(7_000u, summary.Reference);
    }
}

public sealed class BufferHealthTests
{
    [Fact]
    public void CheapStacksDoNotCountAsAHealthyBuffer()
    {
        // Twelve stacks of cheap dye meets the count but is not a trading position.
        Assert.False(ResaleStockPolicy.BufferIsComfortable(12, 12, 60_000, 1_000_000));
        Assert.Equal(940_000ul, ResaleStockPolicy.BufferValueShortfall(60_000, 1_000_000));
    }

    [Fact]
    public void BothTargetsMustBeMet()
    {
        Assert.True(ResaleStockPolicy.BufferIsComfortable(12, 12, 1_200_000, 1_000_000));
        // Value is there but the spread is not.
        Assert.False(ResaleStockPolicy.BufferIsComfortable(3, 12, 5_000_000, 1_000_000));
        Assert.Equal(0ul, ResaleStockPolicy.BufferValueShortfall(1_200_000, 1_000_000));
    }

    [Fact]
    public void AZeroValueTargetLeavesTheStackCountInCharge()
    {
        Assert.True(ResaleStockPolicy.BufferIsComfortable(12, 12, 0, 0));
        Assert.False(ResaleStockPolicy.BufferIsComfortable(11, 12, 0, 0));
    }
}
