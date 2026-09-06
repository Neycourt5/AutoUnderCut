using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class ReinvestmentAndStockPolicyTests
{
    private static ProcurementMarketItem Market(uint quantity, uint weeklyUnits) => new(
        1, "Item",
        [new(1, 1, 1, "Cactuar", 1, 100, quantity, false)],
        [new(1_000, weeklyUnits, false, DateTimeOffset.UtcNow.AddDays(-1))]);

    private static ProcurementRule Rule() => new()
    {
        ItemId = 1, ItemName = "Item", TargetStackSize = 10, MaximumSaleSlots = 8,
    };

    [Fact]
    public void ReinvestmentSpendsTheWalletAndIgnoresThePerTripCap()
    {
        Assert.Equal(995_000u, ResaleStockPolicy.SpendableGil(1_000_000, 5_000, true, 100));
        // With reinvestment off the per-trip cap applies and already-spent gil counts.
        Assert.Equal(100u, ResaleStockPolicy.SpendableGil(1_000_000, 5_000, false, 100));
        Assert.Equal(40u, ResaleStockPolicy.SpendableGil(1_000_000, 5_000, false, 100, 60));
    }

    [Fact]
    public void TheTravelReserveIsAlwaysWithheld()
    {
        Assert.Equal(0u, ResaleStockPolicy.SpendableGil(5_000, 5_000, true, 1_000_000));
        Assert.Equal(0u, ResaleStockPolicy.SpendableGil(0, 5_000, true, 1_000_000));
        Assert.Equal(1u, ResaleStockPolicy.SpendableGil(5_001, 5_000, true, 1_000_000));
    }

    [Fact]
    public void LiquidateOnlyStockIsNeverPurchased()
    {
        var rule = Rule();
        rule.LiquidateOnly = true;
        Assert.Empty(new ProcurementPlannerService()
            .BuildPlan(new([Market(10, 200)], [rule], 500_000, 5, 5, 20, 100)).Orders);

        // The same deal is bought once the item is tradeable stock again.
        Assert.Single(new ProcurementPlannerService()
            .BuildPlan(new([Market(10, 200)], [Rule()], 500_000, 5, 5, 20, 100)).Orders);
    }

    [Fact]
    public void LiquidateOnlyStockIsStillListedFromTheBagsWithNothingHeldBack()
    {
        var rule = Rule();
        rule.LiquidateOnly = true;
        rule.ListFromBags = true;
        rule.BagReserveQuantity = 0;
        Assert.True(ResaleStockPolicy.CanListFromBags(rule, "Dalamud Red Dye", false));
        Assert.Equal(0u, ResaleStockPolicy.BagReserve(rule, "Dalamud Red Dye", 100));
    }

    [Fact]
    public void StockAlreadyOwnedStopsTheSameItemBeingBoughtAgain()
    {
        // 200 units sell weekly and a quarter of that may be held, so 50 units.
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Market(10, 200)], [Rule()], 500_000, 5, 5, 20, 100,
            OwnedStock: [new(1, false, 45, 5)], MaximumWeeklySalesSharePercent: 25m));
        Assert.Empty(plan.Orders);

        var headroom = new ProcurementPlannerService().BuildPlan(new(
            [Market(10, 200)], [Rule()], 500_000, 5, 5, 20, 100,
            OwnedStock: [new(1, false, 20, 2)], MaximumWeeklySalesSharePercent: 25m));
        Assert.Single(headroom.Orders);
    }

    [Fact]
    public void OneTargetStackIsAlwaysAllowedForAnItemWithNoStockYet()
    {
        // A 20-units-per-week minimum and a 25% share would otherwise never admit a
        // single stack, so a qualifying item could never be stocked at all.
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Market(10, 20)], [Rule()], 500_000, 5, 5, 20, 100,
            MaximumWeeklySalesSharePercent: 25m));
        Assert.Single(plan.Orders);
    }
}
