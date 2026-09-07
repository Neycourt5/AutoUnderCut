using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class StockPriorityTests
{
    private static ProcurementMarketItem Market(uint id, string name, uint price, uint weekly, uint? salePrice = null) => new(
        id, name,
        [new(id, id, id, "Cactuar", 1, price, 10, false)],
        [new(salePrice ?? price * 4, weekly, false, DateTimeOffset.UtcNow.AddDays(-1))]);

    private static ProcurementRule Rule(uint id, string name, int tourPriority) => new()
    {
        ItemId = id, ItemName = name, TargetStackSize = 10, MaximumSaleSlots = 8, TourPriority = tourPriority,
    };

    [Fact]
    public void HighVolumeStockIsPreferredWhenOnlyOneSlotIsFree()
    {
        // A dye with a slightly richer margin should not beat the raid consumable
        // that actually turns over; keeping retainers stocked is the objective.
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Market(1, "Caramel Popcorn", 100, 400), Market(2, "General-purpose Dye", 90, 400)],
            [Rule(1, "Caramel Popcorn", 0), Rule(2, "General-purpose Dye", 1)],
            500_000, FreeSaleSlots: 1, FreeInventorySlots: 5, MinimumRoiPercent: 20, MinimumProfitPerUnit: 10));

        var order = Assert.Single(plan.Orders);
        Assert.Equal("Caramel Popcorn", order.ItemName);
    }

    [Fact]
    public void AFullerPlanBeatsARicherOneThatLeavesSlotsEmpty()
    {
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Market(1, "Caramel Popcorn", 100, 400), Market(2, "General-purpose Dye", 90, 400)],
            [Rule(1, "Caramel Popcorn", 0), Rule(2, "General-purpose Dye", 1)],
            500_000, FreeSaleSlots: 4, FreeInventorySlots: 8, MinimumRoiPercent: 20, MinimumProfitPerUnit: 10));

        // Both qualify, so both slots get used rather than one being left open.
        Assert.Equal(2, plan.Orders.Count);
        Assert.Equal("Caramel Popcorn", plan.Orders[0].ItemName);
    }

    [Fact]
    public void ALowerFillMarginAdmitsDealsTheNormalBarRejects()
    {
        // Exactly what the top-up pass relies on: the same board yields nothing at
        // the normal bar and a real, still-profitable buy at the fill bar.
        // Fixed 2,760 resale anchor: 1,500 clears a 10% bar but not a 200% one.
        var market = Market(1, "Caramel Popcorn", 1_500, 400, salePrice: 2_760);
        var rules = new[] { Rule(1, "Caramel Popcorn", 0) };

        Assert.Empty(new ProcurementPlannerService().BuildPlan(new(
            [market], rules, 500_000, 4, 8, MinimumRoiPercent: 200, MinimumProfitPerUnit: 10)).Orders);

        var filled = new ProcurementPlannerService().BuildPlan(new(
            [market], rules, 500_000, 4, 8, MinimumRoiPercent: 10, MinimumProfitPerUnit: 10));
        var order = Assert.Single(filled.Orders);
        Assert.True(order.ExpectedProfit > 0, "a fill buy must still be profitable after fees");
    }
}
