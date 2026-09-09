using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class SalesVelocityPolicyTests
{
    [Fact]
    public void MissingHqRateUsesOnlyValidHqUnitsAcrossSevenDays()
    {
        var now = DateTimeOffset.UtcNow;
        var market = new ProcurementMarketItem(1, "Food", [], [
            new(100, 70, true, now.AddDays(-1)),
            new(100, 7000, false, now.AddDays(-1)),
            new(100, 7000, true, now.AddDays(-8)),
            new(100, 7000, true, now.AddMinutes(1)),
            new(0, 7000, true, now.AddDays(-1)),
        ], NqSalesPerDay: 9000);
        Assert.Equal(10m, SalesVelocityPolicy.DailyUnits(market, true, now));
        Assert.Equal(9000m, SalesVelocityPolicy.DailyUnits(market, false, now));
    }

    [Fact]
    public void KnownZeroDoesNotFallBackToPositiveHistory()
    {
        var market = new ProcurementMarketItem(1, "Food", [],
            [new(100, 700, true, DateTimeOffset.UtcNow.AddDays(-1))], HqSalesPerDay: 0);
        Assert.Equal(0m, SalesVelocityPolicy.DailyUnits(market, true));
    }

    [Fact]
    public void ProfitVelocityBeatsALargerProfitThatSitsInTheSlotForWeeks()
    {
        // Both stacks are worth trading, so both reach the secondary tier and the
        // plan is decided on profit per day of slot occupancy: 8,450 gil turning
        // over in hours is worth more than 17,950 gil that takes five days.
        var now = DateTimeOffset.UtcNow;
        var markets = new[] {
            new ProcurementMarketItem(1, "Slow food", [new(1, 11, 21, "Cactuar", 1, 1_000, 100, false)],
                [new(20_000, 700, false, now.AddDays(-1))], NqSalesPerDay: 20),
            new ProcurementMarketItem(2, "Fast food", [new(2, 12, 22, "Cactuar", 1, 1_000, 100, false)],
                [new(10_000, 700, false, now.AddDays(-1))], NqSalesPerDay: 500),
        };
        var rules = new[] {
            new ProcurementRule { ItemId = 1, ItemName = "Slow food", TourPriority = 0, TargetStackSize = 100 },
            new ProcurementRule { ItemId = 2, ItemName = "Fast food", TourPriority = 5, TargetStackSize = 100 },
        };
        var plan = new ProcurementPlannerService().BuildPlan(new(markets, rules, 500_000, 1, 10,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10, Portfolio: PortfolioGates.Default));
        var order = Assert.Single(plan.Orders);
        Assert.Equal(2u, order.ItemId);
        Assert.Equal(PortfolioTier.Secondary, order.Tier);
        Assert.Equal(500m, order.SalesPerDay);
        Assert.True(order.ExpectedProfit > 0);
    }

    [Fact]
    public void HighVelocityDoesNotBypassBudgetOrProfitGuards()
    {
        var market = new ProcurementMarketItem(1, "Fast dye", [new(1, 11, 21, "Cactuar", 1, 1000, 10, false)],
            [new(1000, 700, false, DateTimeOffset.UtcNow.AddDays(-1))], NqSalesPerDay: 10000);
        var rules = new[] { new ProcurementRule { ItemId = 1, ItemName = "Fast dye", TargetStackSize = 10 } };
        var planner = new ProcurementPlannerService();
        Assert.Empty(planner.BuildPlan(new([market], rules, 500_000, 1, 10,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10)).Orders);
        var profitable = market with { RecentSales = [new(4000, 700, false, DateTimeOffset.UtcNow.AddDays(-1))] };
        Assert.Empty(planner.BuildPlan(new([profitable], rules, 100, 1, 10,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10)).Orders);
    }
}
