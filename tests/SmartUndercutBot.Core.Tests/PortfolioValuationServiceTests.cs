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

    [Fact]
    public void UnlistedStockAddsNetValueOnlyToProjectedWealth()
    {
        PortfolioBagStockEstimate[] bags =
        [
            new(10, "Potion", true, 10, 900, BagValuationSource.HomeMarket, 5m),
            new(20, "Food", true, 5, 600, BagValuationSource.PurchaseCost, 5m),
            new(30, "Dye", false, 3, 0, BagValuationSource.Unknown, 5m),
        ];
        var service = new PortfolioValuationService();
        PortfolioListingEstimate[] listings = [new(1, "Alpha", 0, 10, "Potion", 10, 1_000, 900, true, 5m)];
        PortfolioRetainerBalance[] balances = [new(1, "Alpha", 2_000, 5m)];

        var withoutBags = service.Calculate(listings, balances, 7_000, null, null, true, true, 1);
        var result = service.Calculate(listings, balances, 7_000, null, null, true, true, 1, bags);

        Assert.Equal(12_000ul, result.EstimatedBagGrossValue);
        Assert.Equal(11_400ul, result.EstimatedBagNetValue);
        Assert.Equal(withoutBags.ProjectedWealthAtAsking + 11_400, result.ProjectedWealthAtAsking);
        Assert.Equal(withoutBags.ProjectedWealthMarketAligned + 11_400, result.ProjectedWealthMarketAligned);
        Assert.Equal(withoutBags.CurrentGil, result.CurrentGil);
        Assert.Equal(withoutBags.EstimatedNetMarketAligned, result.EstimatedNetMarketAligned);
        Assert.Equal(withoutBags.Listings, result.Listings);
        Assert.Equal(withoutBags.Units, result.Units);
        Assert.Equal(3, result.BagItemTypes);
        Assert.Equal(18ul, result.BagUnits);
        Assert.Equal(1, result.UnknownBagItemTypes);
        Assert.Equal(3ul, result.UnknownBagUnits);
        Assert.Equal(1, result.CostBasisBagItemTypes);
    }

    [Fact]
    public void UnknownOrZeroPriceNeverInflatesBagValue()
    {
        PortfolioBagStockEstimate[] bags =
        [
            new(10, "Potion", true, 99, 1_000, BagValuationSource.Unknown, 5m),
            new(20, "Food", true, 20, 0, BagValuationSource.HomeMarket, 5m),
            new(30, "Dye", false, 0, 500, BagValuationSource.PurchaseCost, 5m),
        ];

        var result = new PortfolioValuationService().Calculate(
            [], [], 500, null, null, false, false, 0, bags);

        Assert.Equal(0ul, result.EstimatedBagGrossValue);
        Assert.Equal(500ul, result.ProjectedWealthMarketAligned);
        Assert.Equal(2, result.BagItemTypes);
        Assert.Equal(2, result.UnknownBagItemTypes);
        Assert.Equal(119ul, result.UnknownBagUnits);
        Assert.Equal(0, result.CostBasisBagItemTypes);
    }
}
