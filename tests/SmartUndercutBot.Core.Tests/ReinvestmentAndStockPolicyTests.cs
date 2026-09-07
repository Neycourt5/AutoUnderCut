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
    public void TomestoneMaterialsAreListedFromBagsButNeverBought()
    {
        var pelt = new ProcurementRule
        {
            ItemId = 1, ItemName = "Diatryma Pelt", LiquidateOnly = true,
            ListFromBags = true, BagReserveQuantity = 0,
        };
        Assert.True(ResaleStockPolicy.IsTomeMaterial("Diatryma Pelt"));
        Assert.True(ResaleStockPolicy.IsTomeMaterial("yollal extract"));
        Assert.False(ResaleStockPolicy.IsTomeMaterial("Diatryma Skin"));

        // Listed with nothing held back...
        Assert.True(ResaleStockPolicy.CanListFromBags(pelt, pelt.ItemName, false));
        Assert.Equal(0u, ResaleStockPolicy.BagReserve(pelt, pelt.ItemName, 100));
        // ...and never planned as a purchase, at either quality.
        Assert.Empty(new ProcurementPlannerService()
            .BuildPlan(new([Market(10, 200)], [pelt], 500_000, 5, 5, 20, 100)).Orders);
        // Nor walked on the all-world tour.
        Assert.Empty(ResaleStockPolicy.SelectTourRules([pelt], _ => true, 8));
    }

    [Fact]
    public void AnExistingConfigGetsItsFlipsBackOntoTheTourAheadOfMateria()
    {
        // Rules saved before the tour flags existed kept the default priority and
        // were never marked for the tour, so a real config walked only materia.
        var config = new SmartUndercutBot.Configuration
        {
            Version = 30,
            ProcurementRules =
            [
                new() { ItemId = 1, ItemName = "Caramel Popcorn" },
                new() { ItemId = 2, ItemName = "General-purpose Metallic Sky Blue Dye" },
                new() { ItemId = 3, ItemName = "Wide Spectrum #1 Dye" },
                new() { ItemId = 4, ItemName = "Savage Might Materia XI" },
                new() { ItemId = 5, ItemName = "Dalamud Red Dye", LiquidateOnly = true },
            ],
        };
        config.Normalize();

        var byName = config.ProcurementRules.ToDictionary(x => x.ItemName);
        Assert.Equal((true, 0), (byName["Caramel Popcorn"].HuntOnTour, byName["Caramel Popcorn"].TourPriority));
        Assert.Equal((true, 1), (byName["General-purpose Metallic Sky Blue Dye"].HuntOnTour,
            byName["General-purpose Metallic Sky Blue Dye"].TourPriority));
        Assert.Equal((true, 1), (byName["Wide Spectrum #1 Dye"].HuntOnTour,
            byName["Wide Spectrum #1 Dye"].TourPriority));
        Assert.Equal((true, 2), (byName["Savage Might Materia XI"].HuntOnTour,
            byName["Savage Might Materia XI"].TourPriority));
        // Sell-off stock stays off the tour entirely.
        Assert.False(byName["Dalamud Red Dye"].HuntOnTour);

        // And the tour now walks the flips before the materia.
        Assert.Equal(
            ["Caramel Popcorn", "General-purpose Metallic Sky Blue Dye", "Wide Spectrum #1 Dye",
             "Savage Might Materia XI"],
            ResaleStockPolicy.SelectTourRules(config.ProcurementRules, _ => true, 8).Select(x => x.ItemName));
    }

    [Fact]
    public void TheAllWorldTourOnlyWalksMarkedStockInPriorityOrder()
    {
        ProcurementRule Tour(string name, int priority) =>
            new() { ItemId = (uint)name.Length + (uint)priority * 100, ItemName = name, HuntOnTour = true, TourPriority = priority };
        var rules = new List<ProcurementRule>
        {
            Tour("Current Materia XII", 2),
            Tour("General-Purpose Pink Dye", 1),
            Tour("Popoto Potage", 0),
            // Sell-off stock and unmarked rules must never cost the tour a world visit.
            new() { ItemId = 900, ItemName = "Old Materia V", LiquidateOnly = true, HuntOnTour = true },
            new() { ItemId = 901, ItemName = "Something Else" },
        };

        var picked = ResaleStockPolicy.SelectTourRules(rules, _ => true, 8);
        Assert.Equal(["Popoto Potage", "General-Purpose Pink Dye", "Current Materia XII"],
            picked.Select(x => x.ItemName));

        // The cap keeps a tour finishing, and drops the lowest priority first.
        Assert.Equal(["Popoto Potage"], ResaleStockPolicy.SelectTourRules(rules, _ => true, 1).Select(x => x.ItemName));
        Assert.Empty(ResaleStockPolicy.SelectTourRules(rules, x => x.TourPriority > 9, 8));
    }

    [Theory]
    [InlineData("Water Materia XI", true)]
    [InlineData("Savage Aim Materia XII", true)]
    [InlineData("Water Materia X", false)]
    [InlineData("Water Materia V", false)]
    [InlineData("Water Materia I", false)]
    public void OnlyCurrentMateriaGradesAreTradeable(string name, bool tradeable) =>
        Assert.Equal(tradeable, ResaleStockPolicy.IsTradeableMateria(name));

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
    public void HighQualityOnlyRefusesTheNormalFormOfAnItemThatHasBoth()
    {
        // Food and potions exist at both qualities and only the HQ form sells, so a
        // normal-quality listing must not be bought.
        var rule = Rule();
        rule.AllowHighQuality = true;
        Assert.Empty(new ProcurementPlannerService().BuildPlan(new(
            [Market(10, 200)], [rule], 500_000, 5, 5, 20, 100, HighQualityOnly: true)).Orders);
        Assert.Single(new ProcurementPlannerService().BuildPlan(new(
            [Market(10, 200)], [rule], 500_000, 5, 5, 20, 100)).Orders);
    }

    [Fact]
    public void HighQualityOnlyStillBuysItemsThatHaveNoHighQualityForm()
    {
        // Dyes only ever exist at normal quality. Excluding them would mean the
        // preference silently removed a whole category of tradeable stock.
        Assert.Single(new ProcurementPlannerService().BuildPlan(new(
            [Market(10, 200)], [Rule()], 500_000, 5, 5, 20, 100, HighQualityOnly: true)).Orders);
        Assert.True(ResaleStockPolicy.BuyableQuality(Rule(), false, true));
        var both = Rule();
        both.AllowHighQuality = true;
        Assert.False(ResaleStockPolicy.BuyableQuality(both, false, true));
        Assert.True(ResaleStockPolicy.BuyableQuality(both, true, true));
    }

    [Fact]
    public void HighQualityOnlyStillBuysTheHighQualityForm()
    {
        var market = new ProcurementMarketItem(1, "Item",
            [new(1, 1, 1, "Cactuar", 1, 100, 10, true)],
            [new(1_000, 200, true, DateTimeOffset.UtcNow.AddDays(-1))]);
        var rule = Rule();
        rule.AllowHighQuality = true;
        var order = Assert.Single(new ProcurementPlannerService().BuildPlan(new(
            [market], [rule], 500_000, 5, 5, 20, 100, HighQualityOnly: true)).Orders);
        Assert.True(order.IsHighQuality);
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
