using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class ProcurementPlannerServiceTests
{
    private readonly ProcurementPlannerService planner = new();

    [Theory]
    [InlineData(900u, false)]
    [InlineData(2_000u, true)]
    public void RegularShoppingRespectsHomeCompetitionIncludingOversizedStacks(uint homePrice, bool profitable)
    {
        var market = new ProcurementMarketItem(1, "Popcorn",
            [new(1, 10, 20, "Siren", 1, homePrice, 999, true),
             new(1, 11, 21, "Cactuar", 2, 1_000, 99, true)],
            [new(3_000, 99, true, DateTimeOffset.UtcNow)]);
        var plan = planner.BuildPlan(new([market], [new() { ItemId = 1, RequireHighQuality = true }],
            500_000, 5, 5, 20m, 100, HomeWorld: "Siren"));
        if (profitable)
            Assert.Equal(homePrice - 1, Assert.Single(plan.Orders).TargetSalePrice);
        else
            Assert.Empty(plan.Orders);
    }

    [Fact]
    public void RegularShoppingExcludesOwnedRetainersFromResaleAndPurchases()
    {
        var market = new ProcurementMarketItem(1, "Popcorn",
            [new(1, 10, 20, "Siren", 1, 1, 99, true),
             new(1, 11, 21, "Siren", 1, 2_000, 99, true),
             new(1, 12, 20, "Cactuar", 2, 1, 99, true),
             new(1, 13, 22, "Cactuar", 2, 1_000, 99, true)],
            [new(2_500, 99, true, DateTimeOffset.UtcNow)]);
        var plan = planner.BuildPlan(new([market], [new() { ItemId = 1, RequireHighQuality = true }],
            500_000, 5, 5, 20m, 100, HomeWorld: "Siren", OwnedRetainerIds: new HashSet<ulong> { 20 }));
        var order = Assert.Single(plan.Orders);
        Assert.Equal(22ul, order.RetainerId);
        Assert.Equal(1_999u, order.TargetSalePrice);
    }

    [Fact]
    public void OversizedHomeStacksStillSetTheResaleFloor()
    {
        var market = new ProcurementMarketItem(1, "Popcorn",
            [new(1, 10, 20, "Siren", 1, 900, 999, true),
             new(1, 11, 21, "Siren", 1, 2_000, 99, true),
             new(1, 12, 22, "Cactuar", 2, 1_000, 99, true)], []);
        var rule = new ProcurementRule { ItemId = 1, RequireHighQuality = true };
        var plan = planner.BuildLiveMarketPlan(new([market], [rule], "Siren", new HashSet<ulong>(),
            500_000, 5, 5, 20m, 100));
        Assert.Empty(plan.Orders);
    }

    [Fact]
    public void LivePlanRejectsWrongItemAndZeroQuantityAnchors()
    {
        var market = new ProcurementMarketItem(1, "Popcorn",
            [new(2, 10, 20, "Siren", 1, 2_000, 99, true),
             new(1, 11, 21, "Siren", 1, 2_000, 0, true),
             new(1, 12, 22, "Cactuar", 2, 1_000, 99, true)], []);
        var rule = new ProcurementRule { ItemId = 1, RequireHighQuality = true };
        Assert.Empty(planner.BuildLiveMarketPlan(new([market], [rule], "Siren", new HashSet<ulong>(),
            500_000, 5, 5, 20m, 100)).Orders);
    }

    [Fact]
    public void BuildsProfitablePlanWithinBudgetAndSlots()
    {
        var now = DateTimeOffset.UtcNow;
        var market = new ProcurementMarketItem(1, "Caramel Popcorn",
            [
                new(1, 10, 20, "Alpha", 1, 1_000, 99, false),
                new(1, 11, 21, "Beta", 2, 1_100, 99, false),
            ],
            [new(2_000, 99, false, now), new(2_100, 99, false, now)]);
        var rule = new ProcurementRule { ItemId = 1, ItemName = market.ItemName, MaximumSaleSlots = 8 };

        var plan = planner.BuildPlan(new([market], [rule], 150_000, 10, 10, 20m, 100));

        Assert.Single(plan.Orders);
        Assert.Equal(103_950u, plan.TotalCost);
        Assert.Equal(2_050u, plan.Orders[0].TargetSalePrice);
    }

    [Fact]
    public void RejectsPriceThatOnlyLooksCheapWithoutSalesVelocity()
    {
        var market = new ProcurementMarketItem(1, "Slow Item",
            [new(1, 10, 20, "Alpha", 1, 1, 1, false)],
            [new(50_000, 1, false, DateTimeOffset.UtcNow)]);
        var rule = new ProcurementRule { ItemId = 1, MinimumWeeklyUnitsSold = 20 };

        var plan = planner.BuildPlan(new([market], [rule], 1_000_000, 10, 10, 20m, 100));

        Assert.Empty(plan.Orders);
    }

    [Fact]
    public void RespectsPerItemDiversificationLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var listings = Enumerable.Range(1, 10)
            .Select(x => new ProcurementMarketListing(1, (ulong)x, (ulong)x, "Alpha", 1, 1_000, 1, false))
            .ToArray();
        var market = new ProcurementMarketItem(1, "Potion", listings, [new(2_000, 100, false, now)]);
        var rule = new ProcurementRule { ItemId = 1, MaximumSaleSlots = 3 };

        var plan = planner.BuildPlan(new([market], [rule], 1_000_000, 10, 10, 20m, 100));

        Assert.Equal(3, plan.Orders.Count);
    }

    [Fact]
    public void HighQualityOnlyRuleRejectsNqDealsAndUsesHqHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var market = new ProcurementMarketItem(1, "Caramel Popcorn",
            [
                new(1, 10, 20, "Alpha", 1, 100, 99, false),
                new(1, 11, 21, "Alpha", 1, 1_000, 99, true),
            ],
            [
                new(200, 999, false, now),
                new(2_000, 99, true, now),
            ]);
        var rule = new ProcurementRule
        {
            ItemId = 1,
            ItemName = market.ItemName,
            AllowHighQuality = true,
            RequireHighQuality = true,
        };

        var plan = planner.BuildPlan(new([market], [rule], 200_000, 5, 5, 20m, 100));

        var order = Assert.Single(plan.Orders);
        Assert.True(order.IsHighQuality);
        Assert.Equal(1_000u, order.PricePerUnit);
        Assert.Equal(2_000u, order.TargetSalePrice);
    }

    [Fact]
    public void LiveMarketPlanUsesHomeWorldPriceAndIgnoresOwnedListings()
    {
        var market = new ProcurementMarketItem(1, "Caramel Popcorn",
            [
                new(1, 10, 100, "Siren", 1, 1, 99, true),
                new(1, 11, 101, "Siren", 1, 2_000, 99, true),
                new(1, 12, 102, "Cactuar", 2, 1_000, 99, true),
            ], []);
        var rule = new ProcurementRule
        {
            ItemId = 1,
            ItemName = market.ItemName,
            AllowHighQuality = true,
            RequireHighQuality = true,
            MaximumSaleSlots = 8,
        };

        var plan = planner.BuildLiveMarketPlan(new(
            [market], [rule], "Siren", new HashSet<ulong> { 100 },
            500_000, 5, 5, 20m, 100));

        var order = Assert.Single(plan.Orders);
        Assert.Equal("Cactuar", order.WorldName);
        Assert.Equal(2_000u, order.TargetSalePrice);
        Assert.Equal(1_000u, order.PricePerUnit);
    }

    [Fact]
    public void LiveMarketPlanRejectsDealWithoutLiveHomeWorldAnchor()
    {
        var market = new ProcurementMarketItem(1, "Caramel Popcorn",
            [new(1, 12, 102, "Cactuar", 2, 1, 99, true)], []);
        var rule = new ProcurementRule
        {
            ItemId = 1,
            AllowHighQuality = true,
            RequireHighQuality = true,
        };

        var plan = planner.BuildLiveMarketPlan(new(
            [market], [rule], "Siren", new HashSet<ulong>(),
            500_000, 5, 5, 20m, 100));

        Assert.Empty(plan.Orders);
    }

    [Fact]
    public void RejectsListingWhoseBuyerFeeBreaksTheRequiredRoi()
    {
        var now = DateTimeOffset.UtcNow;
        var market = new ProcurementMarketItem(1, "Caramel Popcorn",
            [new(1, 10, 20, "Alpha", 1, 1_550, 99, true)],
            [new(2_000, 99, true, now)]);
        var rule = new ProcurementRule
        {
            ItemId = 1,
            ItemName = market.ItemName,
            AllowHighQuality = true,
            RequireHighQuality = true,
        };

        var plan = planner.BuildPlan(new([market], [rule], 500_000, 5, 5, 20m, 100));

        Assert.Empty(plan.Orders);
    }

    [Fact]
    public void BuyerFeeCountsAgainstTheGilBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var market = new ProcurementMarketItem(1, "Caramel Popcorn",
            [new(1, 10, 20, "Alpha", 1, 1_000, 99, true)],
            [new(2_000, 99, true, now)]);
        var rule = new ProcurementRule
        {
            ItemId = 1,
            ItemName = market.ItemName,
            AllowHighQuality = true,
            RequireHighQuality = true,
        };

        var plan = planner.BuildPlan(new([market], [rule], 100_000, 5, 5, 20m, 100));

        Assert.Empty(plan.Orders);
    }
}
