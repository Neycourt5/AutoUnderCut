using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// The economic objective: a high-quality trading portfolio first, secondary
/// opportunities with what is left, and an empty slot in preference to junk.
/// </summary>
public sealed class PortfolioAllocationTests
{
    private static readonly PortfolioGates Gates = new(
        PreferredTargetPercent: 75m, OpportunisticMaximumPercent: 10m, MinimumProfitPerSaleSlot: 2_500);

    private static ProcurementMarketItem Consumable(uint id, string name, int listings = 2) => new(
        id, name,
        Enumerable.Range(0, listings)
            .Select(i => new ProcurementMarketListing(id, (ulong)(id * 100 + (uint)i), (ulong)(id * 10 + (uint)i),
                "Cactuar", 1, 1_000, 99, true))
            .ToArray(),
        [new(2_000, 2_800, true, DateTimeOffset.UtcNow.AddDays(-1))], HqSalesPerDay: 400m);

    private static ProcurementRule ConsumableRule(uint id, string name) => new()
    {
        ItemId = id, ItemName = name, TargetStackSize = 99, MaximumSaleSlots = 2,
        AllowHighQuality = true, RequireHighQuality = true, PreferredStock = true, TourPriority = 0,
    };

    // Technically profitable and superb on ROI, but a stack is worth a few
    // thousand gil - exactly the stock that used to crowd out the retainers.
    private static ProcurementMarketItem Dye(uint id, string name, int listings = 3) => new(
        id, name,
        Enumerable.Range(0, listings)
            .Select(i => new ProcurementMarketListing(id, (ulong)(id * 100 + (uint)i), (ulong)(id * 10 + (uint)i),
                "Cactuar", 1, 90, 20, false))
            .ToArray(),
        [new(400, 2_800, false, DateTimeOffset.UtcNow.AddDays(-1))], NqSalesPerDay: 400m);

    private static ProcurementRule DyeRule(uint id, string name) => new()
    {
        ItemId = id, ItemName = name, TargetStackSize = 20, MaximumSaleSlots = 2, TourPriority = 1,
    };

    [Fact]
    public void ProfitableJunkCannotCrowdOutTheCuratedConsumables()
    {
        var markets = new List<ProcurementMarketItem>();
        var rules = new List<ProcurementRule>();
        foreach (var (id, name) in new[]
                 {
                     (1u, "Caramel Popcorn"), (2u, "Popoto Potage"),
                     (3u, "Grade 4 Gemdraught of Strength"), (4u, "Grade 4 Gemdraught of Mind"),
                 })
        {
            markets.Add(Consumable(id, name));
            rules.Add(ConsumableRule(id, name));
        }
        for (var id = 10u; id < 16u; id++)
        {
            markets.Add(Dye(id, $"Dye {id}"));
            rules.Add(DyeRule(id, $"Dye {id}"));
        }

        var plan = new ProcurementPlannerService().BuildPlan(new(markets, rules,
            GilBudget: 5_000_000, FreeSaleSlots: 12, FreeInventorySlots: 30,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            Portfolio: Gates, PortfolioCapacitySlots: 12));

        var core = plan.Orders.Count(x => x.Tier == PortfolioTier.Core);
        var opportunistic = plan.Orders.Count(x => x.Tier == PortfolioTier.Opportunistic);
        Assert.Equal(8, core);
        Assert.Equal(1, opportunistic);
        Assert.True(core > plan.Orders.Count / 2, "curated consumables must hold the majority of the portfolio");
        // Twelve slots were free and dozens of profitable dye listings were
        // available; the extra slots stay empty rather than fill with junk.
        Assert.Equal(9, plan.Orders.Count);
        Assert.Equal(8, plan.Summary.CoreSlots);
        Assert.Equal(9, plan.Summary.CoreTarget);
        Assert.Equal(1, plan.Summary.OpportunisticCap);
    }

    [Fact]
    public void AHighRoiDyeDoesNotBeatAHighValueConsumableForTheLastSlot()
    {
        var dye = Dye(10, "Yellow Dye", listings: 1) with
        {
            Listings = [new(10, 1_000, 100, "Cactuar", 1, 10, 20, false)],
        };
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Consumable(1, "Caramel Popcorn", listings: 1), dye],
            [ConsumableRule(1, "Caramel Popcorn"), DyeRule(10, "Yellow Dye")],
            GilBudget: 5_000_000, FreeSaleSlots: 1, FreeInventorySlots: 10,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            Portfolio: Gates, PortfolioCapacitySlots: 20));

        var order = Assert.Single(plan.Orders);
        Assert.Equal("Caramel Popcorn", order.ItemName);
        // The dye really is the better percentage return, and it is still rejected.
        var rejectedDye = Assert.Single(plan.DecisionLog, x => x.ItemName == "Yellow Dye");
        Assert.False(rejectedDye.Selected);
        Assert.True(rejectedDye.RoiPercent > order.RoiPercent,
            "the dye should win on ROI and lose on portfolio value");
        Assert.True(rejectedDye.ExpectedProfit < order.ExpectedProfit);
    }

    [Fact]
    public void ListedOpportunisticStockConsumesTheOpportunisticCap()
    {
        var planner = new ProcurementPlannerService();
        ProcurementPlan Build(IReadOnlyList<StockExposure> owned) => planner.BuildPlan(new(
            [Dye(10, "Yellow Dye", listings: 2)], [DyeRule(10, "Yellow Dye")],
            GilBudget: 5_000_000, FreeSaleSlots: 4, FreeInventorySlots: 10,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            OwnedStock: owned, Portfolio: Gates, PortfolioCapacitySlots: 20));

        // Two of the twenty-slot portfolio may be opportunistic.
        Assert.Equal(2, Build([]).Orders.Count);
        Assert.Single(Build([new(999, false, 20, 1, PortfolioTier.Opportunistic)]).Orders);
        Assert.Empty(Build([new(999, false, 40, 2, PortfolioTier.Opportunistic)]).Orders);
        // Secondary and core holdings must not consume the opportunistic cap.
        Assert.Equal(2, Build([new(999, false, 40, 2, PortfolioTier.Secondary)]).Orders.Count);
    }

    [Fact]
    public void ListedPreferredStockCountsTowardThePreferredTarget()
    {
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Consumable(1, "Caramel Popcorn", listings: 1)], [ConsumableRule(1, "Caramel Popcorn")],
            GilBudget: 5_000_000, FreeSaleSlots: 4, FreeInventorySlots: 10,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            OwnedStock: [new(1, true, 990, 10, PortfolioTier.Core), new(999, false, 20, 1, PortfolioTier.Opportunistic)],
            Portfolio: Gates, PortfolioCapacitySlots: 20));

        // Ten preferred slots are already listed, so the summary counts them and
        // reports the remaining deficit against the fifteen-slot target.
        Assert.Equal(10, plan.Summary.CoreSlots);
        Assert.Equal(15, plan.Summary.CoreTarget);
        Assert.Equal(5, plan.Summary.CoreDeficit);
        Assert.Equal(1, plan.Summary.OpportunisticSlots);
        Assert.Equal(1, plan.Summary.OpportunisticHeadroom);
    }

    [Fact]
    public void AnEmptySlotIsPreferredToJunkWhenNothingGoodQualifies()
    {
        // Six free slots, plenty of gil, and a board full of profitable dye. The
        // opportunistic cap is already spent, so nothing is bought at all.
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [Dye(10, "Yellow Dye"), Dye(11, "Red Dye"), Dye(12, "Blue Dye")],
            [DyeRule(10, "Yellow Dye"), DyeRule(11, "Red Dye"), DyeRule(12, "Blue Dye")],
            GilBudget: 5_000_000, FreeSaleSlots: 6, FreeInventorySlots: 20,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            OwnedStock: [new(999, false, 40, 2, PortfolioTier.Opportunistic)],
            Portfolio: Gates, PortfolioCapacitySlots: 20));

        Assert.Empty(plan.Orders);
        Assert.Contains(plan.DecisionLog, x => !x.Selected && x.Reason.Contains("opportunistic portfolio cap"));
    }

    [Fact]
    public void LowSlotValueIsRejectedEvenWithHeadroomAndExcellentRoi()
    {
        // A 340% return on a stack worth 2,000 gil is not worth a retainer slot.
        var market = new ProcurementMarketItem(10, "Yellow Dye",
            [new(10, 1, 1, "Cactuar", 1, 20, 20, false)],
            [new(120, 2_800, false, DateTimeOffset.UtcNow.AddDays(-1))], NqSalesPerDay: 400m);
        var plan = new ProcurementPlannerService().BuildPlan(new(
            [market], [DyeRule(10, "Yellow Dye")],
            GilBudget: 5_000_000, FreeSaleSlots: 6, FreeInventorySlots: 20,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            Portfolio: Gates, PortfolioCapacitySlots: 20));

        Assert.Empty(plan.Orders);
        Assert.Contains(plan.DecisionLog, x => !x.Selected && x.Reason.Contains("insufficient slot value"));
    }

    [Fact]
    public void StrongSecondaryStockFillsCapacityWhenNoPreferredStockIsAvailable()
    {
        // Not pinned by hand, but genuinely valuable and fast moving: real trading
        // stock is allowed to use the capacity the curated list cannot fill.
        var market = new ProcurementMarketItem(20, "Grade 8 Tincture of Strength",
            Enumerable.Range(0, 5)
                .Select(i => new ProcurementMarketListing(20, (ulong)i, (ulong)(100 + i), "Cactuar", 1, 4_000, 99, true))
                .ToArray(),
            [new(9_000, 2_100, true, DateTimeOffset.UtcNow.AddDays(-1))], HqSalesPerDay: 300m);
        var rule = new ProcurementRule
        {
            ItemId = 20, ItemName = "Grade 8 Tincture of Strength", TargetStackSize = 99,
            MaximumSaleSlots = 8, AllowHighQuality = true, RequireHighQuality = true,
        };

        var plan = new ProcurementPlannerService().BuildPlan(new(
            [market], [rule], GilBudget: 5_000_000, FreeSaleSlots: 4, FreeInventorySlots: 20,
            MinimumRoiPercent: 20, MinimumProfitPerUnit: 10,
            Portfolio: Gates, PortfolioCapacitySlots: 20));

        Assert.Equal(4, plan.Orders.Count);
        Assert.All(plan.Orders, x => Assert.Equal(PortfolioTier.Secondary, x.Tier));
    }

    [Fact]
    public void TheFillMarginPassCannotReachOpportunisticStock()
    {
        // What TopUpEmptySaleSlots does: the same board, at the lower fill margin,
        // with the opportunistic cap set to zero.
        var market = new ProcurementMarketItem(10, "Yellow Dye",
            [new(10, 1, 1, "Cactuar", 1, 780, 20, false)],
            [new(999, 2_800, false, DateTimeOffset.UtcNow.AddDays(-1))], NqSalesPerDay: 400m);
        var rules = new[] { DyeRule(10, "Yellow Dye") };
        var planner = new ProcurementPlannerService();

        // It clears the lower margin and the slot-value gate on its own.
        var permissive = planner.BuildPlan(new([market], rules, 5_000_000, 4, 20,
            MinimumRoiPercent: 10, MinimumProfitPerUnit: 10,
            Portfolio: Gates with { OpportunisticMaximumPercent = 100m }, PortfolioCapacitySlots: 20));
        Assert.Single(permissive.Orders);
        Assert.Equal(PortfolioTier.Opportunistic, permissive.Orders[0].Tier);

        // The fill pass still refuses it.
        var fill = planner.BuildPlan(new([market], rules, 5_000_000, 4, 20,
            MinimumRoiPercent: 10, MinimumProfitPerUnit: 10,
            Portfolio: Gates with { OpportunisticMaximumPercent = 0m }, PortfolioCapacitySlots: 20));
        Assert.Empty(fill.Orders);
    }

    [Fact]
    public void TierClassificationSeparatesRealTradingStockFromArbitrage()
    {
        const ulong dyeStack = 40_000;      // twenty units at two thousand gil
        const ulong consumableStack = 1_200_000; // ninety-nine units of raid food

        // Pinned stock is core regardless of what the numbers say today.
        Assert.Equal(PortfolioTier.Core,
            PortfolioPolicy.ClassifyCandidate(true, 0m, 0, 0, Gates));
        // A fast, valuable, profitable unpinned item earns secondary.
        Assert.Equal(PortfolioTier.Secondary,
            PortfolioPolicy.ClassifyCandidate(false, 150m, consumableStack, 400_000, Gates));
        // A fast, profitable, but low-value stack stays opportunistic: it moves
        // little capital however good the percentage looks.
        Assert.Equal(PortfolioTier.Opportunistic,
            PortfolioPolicy.ClassifyCandidate(false, 800m, dyeStack, 25_000, Gates));
        // Valuable but nothing is buying it.
        Assert.Equal(PortfolioTier.Opportunistic,
            PortfolioPolicy.ClassifyCandidate(false, 2m, consumableStack, 400_000, Gates));
        // Valuable and fast, but not worth the slot it would occupy.
        Assert.Equal(PortfolioTier.Opportunistic,
            PortfolioPolicy.ClassifyCandidate(false, 150m, consumableStack, 1_000, Gates));

        // Holdings: profit is already sunk, so only value and demand decide.
        Assert.Equal(PortfolioTier.Core, PortfolioPolicy.ClassifyHolding(true, 0m, 0));
        Assert.Equal(PortfolioTier.Secondary, PortfolioPolicy.ClassifyHolding(false, 150m, consumableStack));
        Assert.Equal(PortfolioTier.Opportunistic, PortfolioPolicy.ClassifyHolding(false, 800m, dyeStack));
        // Nothing known about an item means it does not get the benefit of the doubt.
        Assert.Equal(PortfolioTier.Opportunistic, PortfolioPolicy.ClassifyHolding(false, 0m, 0));
    }

    [Fact]
    public void TurnoverAndProfitVelocityAreClampedForTinyAndDeadListings()
    {
        Assert.Equal(PortfolioPolicy.MinimumDaysToSell, PortfolioPolicy.DaysToSell(1, 10_000m));
        Assert.Equal(PortfolioPolicy.MaximumDaysToSell, PortfolioPolicy.DaysToSell(99, 0m));
        Assert.Equal(PortfolioPolicy.MaximumDaysToSell, PortfolioPolicy.DaysToSell(0, 5m));
        Assert.Equal(5m, PortfolioPolicy.DaysToSell(100, 20m));
        // A one-unit listing cannot manufacture an unbounded score.
        Assert.Equal(4_000m, PortfolioPolicy.ProfitVelocity(1_000, PortfolioPolicy.DaysToSell(1, 10_000m)));
    }
}
