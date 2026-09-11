using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class AdaptiveShoppingTests
{
    [Theory]
    [InlineData(60, 6, 0, 12)] // mostly materia: build useful replacements
    [InlineData(60, 6, 12, 0)] // replacements already bought: sell them first
    [InlineData(60, 44, 0, 1)] // nearly at the 75% target
    [InlineData(60, 45, 0, 0)] // healthy listed mix: wait for a batch of sales
    [InlineData(20, 0, 0, 5)]
    [InlineData(100, 0, 0, 20)]
    [InlineData(0, 0, 0, 0)]
    public void ReplacementCapacityFollowsTheListedMixAndExistingBuffer(
        int capacity, int listed, int bags, int expected) =>
        Assert.Equal(expected, AdaptiveShoppingPolicy.PreferredRestockSlots(capacity, listed, bags, 75m));

    [Theory]
    [InlineData(false, 100_000u, 10)]
    [InlineData(true, 100_000u, 10)]
    [InlineData(false, 4_000_000u, 0)]
    [InlineData(true, 4_000_000u, 0)]
    public void ExtraCapitalAndCapacityCannotLeakToNonPreferredStock(bool live, uint otherBudget, int otherSlots)
    {
        var markets = new[] { Market(1, 12_000), Market(2, 10_000) };
        var rules = new[] { Rule(1, true), Rule(2, false) };
        var planner = new ProcurementPlannerService();
        var plan = live
            ? planner.BuildLiveMarketPlan(new(markets, rules, "Siren", new HashSet<ulong>(),
                4_000_000, 10, 20, 20m, 100, Economics: ProcurementEconomicPolicy.Default,
                NonPreferredGilBudget: otherBudget, NonPreferredSaleSlots: otherSlots))
            : planner.BuildPlan(new(markets, rules, 4_000_000, 10, 20, 20m, 100, HomeWorld: "Siren",
                Economics: ProcurementEconomicPolicy.Default,
                NonPreferredGilBudget: otherBudget, NonPreferredSaleSlots: otherSlots));

        Assert.NotEmpty(plan.Orders);
        Assert.All(plan.Orders, o => Assert.Equal(1u, o.ItemId));
        Assert.True(plan.TotalCost > 1_000_000); // the old global 20% cap would reject every core stack
        Assert.True(plan.TotalCost <= 4_000_000);
        Assert.All(plan.Orders, o => Assert.InRange(o.NetRoiPercent, 10m, 20m));
    }

    [Fact]
    public void NonPreferredBudgetIsSharedAcrossTheBasketIncludingBuyerFees()
    {
        var planner = new ProcurementPlannerService();
        var plan = planner.BuildPlan(new([Market(1, 12_000), Market(2, 10_000)],
            [Rule(1, true), Rule(2, false)], 8_000_000, 10, 20, 20m, 100, HomeWorld: "Siren",
            Economics: ProcurementEconomicPolicy.Default, NonPreferredGilBudget: 2_000_000));
        var other = Assert.Single(plan.Orders, o => o.ItemId == 2);
        Assert.Equal(1_039_500UL, other.CapitalAtRisk);
        Assert.Contains(plan.Orders, o => o.ItemId == 1);
        Assert.True(plan.TotalCost > 2_000_000);
    }

    private static ProcurementRule Rule(uint item, bool preferred) => new()
    {
        ItemId = item, ItemName = $"Stock {item}", PreferredStock = preferred,
        RequireHighQuality = true, AllowHighQuality = true, TargetStackSize = 99, MaximumSaleSlots = 20,
    };

    private static ProcurementMarketItem Market(uint item, uint cost) => new(item, $"Stock {item}",
        [new(item, 1, 1, "Siren", 1, 15_000, 99, true),
         .. Enumerable.Range(2, 3).Select(i => new ProcurementMarketListing(item, (ulong)i, (ulong)i,
             "Cactuar", 2, cost, 99, true))],
        Enumerable.Range(1, 3).Select(i => new ProcurementSale(15_000, 700, true, DateTimeOffset.UtcNow.AddDays(-i))).ToArray(),
        HqSalesPerDay: 300m);
}
