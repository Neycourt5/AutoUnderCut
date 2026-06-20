using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class ProcurementPlannerServiceTests
{
    private readonly ProcurementPlannerService planner = new();

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
        Assert.Equal(99_000u, plan.TotalCost);
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
}
