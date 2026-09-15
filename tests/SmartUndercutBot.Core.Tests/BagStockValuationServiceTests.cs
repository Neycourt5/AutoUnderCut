using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class BagStockValuationServiceTests
{
    [Fact]
    public void AggregatesPhysicalStacksAndReservesOnceBeforeValuingFutureSaleStock()
    {
        PortfolioBagHolding[] holdings =
        [
            new(10, "Grade 4 Gemdraught of Strength", true, 99),
            new(10, "Grade 4 Gemdraught of Strength", true, 99),
            new(10, "Grade 4 Gemdraught of Strength", true, 99),
        ];
        ProcurementRule[] rules =
        [
            new() { ItemId = 10, RequireHighQuality = true, MaximumSaleSlots = 1 },
        ];

        var result = BagStockValuationService.Estimate(holdings, rules, 100,
            [new(10, true, 4_700, 3_000)], 5m);

        var potion = Assert.Single(result);
        Assert.Equal(197u, potion.Quantity);
        Assert.Equal(4_700u, potion.EstimatedUnitPrice);
        Assert.Equal(BagValuationSource.HomeMarket, potion.Source);
    }

    [Fact]
    public void ExcludesUnrelatedGearDisabledRulesDisallowedQualityAndReservedStock()
    {
        PortfolioBagHolding[] holdings =
        [
            new(10, "Grade 4 Gemdraught of Strength", false, 99),
            new(20, "Caramel Popcorn", true, 99),
            new(30, "Armour", true, 1),
            new(40, "Dye", false, 5),
            new(50, "Other Dye", false, 8),
        ];
        ProcurementRule[] rules =
        [
            new() { ItemId = 40, Enabled = false, ListFromBags = true },
            new() { ItemId = 50, ListFromBags = false },
        ];

        Assert.Empty(BagStockValuationService.Estimate(holdings, rules, 100, [], 5m));
    }

    [Fact]
    public void PreservesQualityAndUsesPurchaseCostOnlyWhenHomeEstimateIsMissing()
    {
        PortfolioBagHolding[] holdings =
        [
            new(10, "Dye", false, 7),
            new(20, "Grade 4 Gemdraught of Mind", true, 120),
            new(30, "Diatryma Pelt", false, 40),
        ];
        ProcurementRule[] rules =
        [
            new() { ItemId = 10, ListFromBags = true, BagReserveQuantity = 2 },
            new() { ItemId = 30, ListFromBags = true, LiquidateOnly = true, BagReserveQuantity = 0 },
        ];
        PortfolioBagPrice[] prices =
        [
            new(10, false, 0, 300),
            new(20, false, 4_700, 3_000),
            new(30, false, 800, 400),
        ];

        var result = BagStockValuationService.Estimate(holdings, rules, 100, prices, 5m);

        var dye = Assert.Single(result, x => x.ItemId == 10);
        Assert.Equal(5u, dye.Quantity);
        Assert.Equal(300u, dye.EstimatedUnitPrice);
        Assert.Equal(BagValuationSource.PurchaseCost, dye.Source);
        var potion = Assert.Single(result, x => x.ItemId == 20);
        Assert.Equal(20u, potion.Quantity);
        Assert.Equal(0u, potion.EstimatedUnitPrice);
        Assert.Equal(BagValuationSource.Unknown, potion.Source);
        var pelt = Assert.Single(result, x => x.ItemId == 30);
        Assert.Equal(40u, pelt.Quantity);
        Assert.Equal(800u, pelt.EstimatedUnitPrice);
        Assert.Equal(BagValuationSource.HomeMarket, pelt.Source);
    }
}
