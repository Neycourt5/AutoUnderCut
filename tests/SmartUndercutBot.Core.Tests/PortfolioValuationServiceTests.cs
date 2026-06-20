using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class PortfolioValuationServiceTests
{
    [Fact]
    public void CalculatesGrossNetAndProjectedWealthUsingPerRetainerFees()
    {
        var service = new PortfolioValuationService();
        PortfolioListingEstimate[] listings =
        [
            new(1, "Alpha", 0, 10, "Potion", 10, 1_000, 900, true, 5m),
            new(2, "Beta", 0, 20, "Food", 5, 2_000, 1_800, true, 3m),
        ];
        PortfolioRetainerBalance[] balances =
        [
            new(1, "Alpha", 1_000, 5m),
            new(2, "Beta", 2_000, 3m),
        ];

        var result = service.Calculate(listings, balances, 7_000, null, null, true, true, 2);

        Assert.Equal(20_000ul, result.GrossAskingValue);
        Assert.Equal(18_000ul, result.EstimatedGrossValue);
        Assert.Equal(19_200ul, result.EstimatedNetAtAsking);
        Assert.Equal(17_280ul, result.EstimatedNetMarketAligned);
        Assert.Equal(10_000ul, result.CurrentGil);
        Assert.Equal(29_200ul, result.ProjectedWealthAtAsking);
        Assert.Equal(27_280ul, result.ProjectedWealthMarketAligned);
    }

    [Fact]
    public void SellerFeeRoundsDownLikeTheGameTaxDisplay()
    {
        Assert.Equal(561_291ul, PortfolioValuationService.NetAfterSellerFee(590_832, 5m));
    }

    [Fact]
    public void IncludesEmptyRetainerGilWithoutInventingListings()
    {
        var result = new PortfolioValuationService().Calculate(
            [], [new(1, "Empty", 123_456, 0m)], 500, null, null, true, true, 1);

        Assert.Equal(123_956ul, result.CurrentGil);
        Assert.Single(result.Retainers);
        Assert.Equal(0, result.Retainers[0].Listings);
    }
}
