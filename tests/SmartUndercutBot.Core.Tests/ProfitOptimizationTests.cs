using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// The economics the planner is supposed to express: capital belongs in
/// high-value, high-volume markets that reliably sell, ROI is a floor rather than
/// a ranking key, and inventory is sized against a market's own demand.
///
/// Every fixture here is deterministic - fixed prices, fixed velocities, fixed
/// policy - so the numbers in the assertions are the numbers the bot would use.
/// </summary>
public sealed class ProfitOptimizationTests
{
    private readonly ProcurementPlannerService planner = new();

    /// <summary>The shipped economic policy and portfolio shape.</summary>
    private static ProcurementEconomicPolicy Policy => ProcurementEconomicPolicy.Default;
    private static PortfolioGates Gates => new(75m, 10m, 2_500);
    private static FeeModel Fees => FeeModel.Default;

    private static IReadOnlyList<ProcurementSale> History(uint price, uint quantity, bool hq)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(1, 4)
            .Select(i => new ProcurementSale(price, quantity, hq, now.AddDays(-i * 0.5)))
            .ToArray();
    }

    private static ProcurementRule Rule(uint id, string name, int stack, bool preferred, int slots = 8) => new()
    {
        ItemId = id,
        ItemName = name,
        TargetStackSize = stack,
        MaximumSaleSlots = slots,
        MinimumWeeklyUnitsSold = 20,
        PreferredStock = preferred,
        AllowHighQuality = true,
        RequireHighQuality = true,
    };

    private static ProcurementRule NqRule(uint id, string name, int stack, int slots = 2) => new()
    {
        ItemId = id,
        ItemName = name,
        TargetStackSize = stack,
        MaximumSaleSlots = slots,
        MinimumWeeklyUnitsSold = 20,
    };

    private ProcurementPlan Plan(
        IReadOnlyList<ProcurementMarketItem> markets,
        IReadOnlyList<ProcurementRule> rules,
        uint budget,
        int saleSlots = 20,
        IReadOnlyList<StockExposure>? owned = null,
        int capacitySlots = 60) =>
        planner.BuildPlan(new(markets, rules, budget, saleSlots, 60, 20m, 100,
            HomeWorld: "Siren",
            OwnedStock: owned,
            MaximumWeeklySalesSharePercent: 25m,
            Portfolio: Gates,
            PortfolioCapacitySlots: capacitySlots,
            Economics: Policy));

    // ---------------------------------------------------------------------
    // 1. A spectacular percentage on a cheap stack must lose to real gil.
    // ---------------------------------------------------------------------

    [Fact]
    public void HighRoiTrinketLosesToTheProfitableConsumable()
    {
        // Dye: 24,108 gil buys 101,272 gil of profit - about 420% - but it is 20
        // units of cheap stock. Gemdraught: 1,559,250 gil buys 462,726, about 30%.
        var gemdraught = new ProcurementMarketItem(1, "Grade 4 Gemdraught",
            [new(1, 100, 200, "Siren", 1, 21_500, 99, true),
             new(1, 101, 201, "Cactuar", 2, 15_000, 99, true)],
            History(22_000, 70, true), HqSalesPerDay: 40m);
        var dye = new ProcurementMarketItem(2, "General-Purpose Dye",
            [new(2, 110, 210, "Siren", 1, 6_600, 20, false),
             new(2, 111, 211, "Cactuar", 2, 1_148, 20, false)],
            History(6_600, 60, false), NqSalesPerDay: 31m);

        var plan = Plan([gemdraught, dye], [Rule(1, "Grade 4 Gemdraught", 99, true), NqRule(2, "General-Purpose Dye", 20)],
            budget: 5_000_000);

        var lead = plan.Orders[0];
        Assert.Equal(1u, lead.ItemId);
        Assert.InRange(lead.ExpectedProfit, 400_000u, 500_000u);
        Assert.InRange(lead.NetRoiPercent, 25m, 35m);

        // The dye is the better percentage and still ranks below, because ranking is
        // gil per day, not percentage return.
        var trinket = plan.Orders.FirstOrDefault(x => x.ItemId == 2);
        if (trinket is not null)
        {
            Assert.True(trinket.NetRoiPercent > lead.NetRoiPercent * 10m);
            Assert.True(trinket.AllocationScore < lead.AllocationScore);
        }
        // Effectively all of the capital goes to the consumable.
        var consumableCapital = plan.Orders.Where(x => x.ItemId == 1).Sum(x => (decimal)x.CapitalAtRisk);
        Assert.True(consumableCapital / plan.TotalCost > 0.95m,
            $"consumable took {consumableCapital:N0} of {plan.TotalCost:N0}");
    }

    // ---------------------------------------------------------------------
    // 2. A big wallet and a hungry market should mean several stacks.
    // ---------------------------------------------------------------------

    [Fact]
    public void PopcornAbsorbsSeveralMillionGilWhileCoverageIsShort()
    {
        // 100 units a day, 80 units held: 0.8 days of cover against a 3-day target,
        // and a fixed two-slot rule that must not be what decides this.
        var listings = Enumerable.Range(0, 6)
            .Select(i => new ProcurementMarketListing(1, (ulong)(100 + i), (ulong)(200 + i), "Cactuar", 2, 15_000, 99, true))
            .Prepend(new ProcurementMarketListing(1, 10, 20, "Siren", 1, 21_500, 99, true))
            .ToArray();
        var popcorn = new ProcurementMarketItem(1, "Caramel Popcorn", listings,
            History(22_000, 175, true), HqSalesPerDay: 100m);

        var plan = Plan([popcorn], [Rule(1, "Caramel Popcorn", 99, preferred: true, slots: 2)],
            budget: 15_000_000, owned: [new(1, true, 80, 1, PortfolioTier.Core)]);

        // Target 300 units, overshoot ceiling 400: 80 -> 179 -> 278 -> 377, then stop.
        Assert.Equal(3, plan.Orders.Count);
        Assert.True(plan.TotalCost > 4_000_000, $"deployed only {plan.TotalCost:N0} gil");
        Assert.All(plan.Orders, x => Assert.Equal(PortfolioTier.Core, x.Tier));
        // The rule allows two sale slots. Demand allows more, and demand wins.
        Assert.True(plan.Orders.Count > 2);
        Assert.Equal(0.8m, Math.Round(plan.Orders[0].InventoryCoverageDays, 2));
        Assert.True(plan.Orders[^1].CoverageDaysAfterPurchase <= 4m);
    }

    // ---------------------------------------------------------------------
    // 3. Preferred is a preference, not permission to build dead inventory.
    // ---------------------------------------------------------------------

    [Fact]
    public void OverstockedCoreItemStopsBuyingHoweverGoodTheMarketLooks()
    {
        var popcorn = new ProcurementMarketItem(1, "Caramel Popcorn",
            [new(1, 10, 20, "Siren", 1, 21_500, 99, true),
             new(1, 11, 21, "Cactuar", 2, 15_000, 99, true)],
            History(22_000, 175, true), HqSalesPerDay: 100m);

        // Five days of stock against a three-day target.
        var plan = Plan([popcorn], [Rule(1, "Caramel Popcorn", 99, preferred: true)],
            budget: 15_000_000, owned: [new(1, true, 500, 6, PortfolioTier.Core)]);

        Assert.Empty(plan.Orders);
        Assert.Contains(plan.DecisionLog, x => !x.Selected && x.Reason.Contains("demand coverage reached"));
    }

    [Fact]
    public void SlowMovingPreferredItemCannotStockpileOnPreferenceAlone()
    {
        // Three units a day, five days already held: the classic dead-inventory case.
        var potion = new ProcurementMarketItem(1, "Slow Preferred Potion",
            [new(1, 10, 20, "Siren", 1, 21_500, 99, true),
             new(1, 11, 21, "Cactuar", 2, 12_000, 99, true)],
            History(22_000, 6, true), HqSalesPerDay: 3m);

        var plan = Plan([potion], [Rule(1, "Slow Preferred Potion", 99, preferred: true)],
            budget: 15_000_000, owned: [new(1, true, 15, 1, PortfolioTier.Core)]);

        Assert.Empty(plan.Orders);
    }

    // ---------------------------------------------------------------------
    // 4. A thin margin on a fast, valuable market beats a fat one on a slow market.
    // ---------------------------------------------------------------------

    [Fact]
    public void ThinMarginFastMoverOutranksSlowerHighRoiStock()
    {
        var food = new ProcurementMarketItem(1, "Popoto Potage",
            [new(1, 10, 20, "Siren", 1, 21_500, 99, true),
             new(1, 11, 21, "Cactuar", 2, 17_600, 99, true)],
            History(22_000, 210, true), HqSalesPerDay: 120m);
        var slower = new ProcurementMarketItem(2, "Slow High-Margin Stock",
            [new(2, 20, 30, "Siren", 1, 9_001, 99, true),
             new(2, 21, 31, "Cactuar", 2, 6_500, 99, true)],
            History(9_200, 21, true), HqSalesPerDay: 12m);

        // One slot: the ranking alone has to pick the winner.
        var plan = Plan([food, slower],
            [Rule(1, "Popoto Potage", 99, preferred: true), Rule(2, "Slow High-Margin Stock", 99, preferred: false)],
            budget: 5_000_000, saleSlots: 1);

        var order = Assert.Single(plan.Orders);
        Assert.Equal(1u, order.ItemId);
        // It won on roughly a 10% margin, well under the other candidate's 25%.
        Assert.InRange(order.NetRoiPercent, 8m, 15m);
        var rejected = Assert.Single(plan.DecisionLog, x => x.ItemId == 2 && !x.Selected);
        Assert.True(rejected.RoiPercent > 20m);
        Assert.True(order.ExpectedGilPerDay > rejected.ExpectedGilPerDay);
    }

    // ---------------------------------------------------------------------
    // 5. A bad deal is still a bad deal on a preferred item.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(19_000u, false)] // ~2.4% net: under the 8% absolute floor.
    [InlineData(20_500u, false)] // Net proceeds do not even cover landed cost.
    [InlineData(15_000u, true)]  // The same fixture at a price that genuinely works.
    public void PreferredStockIsNotAnExcuseForAThinOrLosingDeal(uint unitPrice, bool expected)
    {
        var gemdraught = new ProcurementMarketItem(1, "Grade 4 Gemdraught",
            [new(1, 10, 20, "Siren", 1, 21_500, 99, true),
             new(1, 11, 21, "Cactuar", 2, unitPrice, 99, true)],
            History(22_000, 70, true), HqSalesPerDay: 40m);

        var plan = Plan([gemdraught], [Rule(1, "Grade 4 Gemdraught", 99, preferred: true)], budget: 15_000_000);
        Assert.Equal(expected, plan.Orders.Count > 0);
    }

    [Fact]
    public void PreferredStockWithNoUsableResaleAnchorIsRejected()
    {
        // No home-world competition and no sale history: nothing to price against.
        var gemdraught = new ProcurementMarketItem(1, "Grade 4 Gemdraught",
            [new(1, 11, 21, "Cactuar", 2, 15_000, 99, true)], [], HqSalesPerDay: 40m);
        Assert.Empty(Plan([gemdraught], [Rule(1, "Grade 4 Gemdraught", 99, preferred: true)], 15_000_000).Orders);
    }

    // ---------------------------------------------------------------------
    // 6. Cheap arbitrage stays a side profit.
    // ---------------------------------------------------------------------

    [Fact]
    public void SpectacularDyesCannotCrowdOutTheConsumablePortfolio()
    {
        var markets = new List<ProcurementMarketItem>
        {
            new(1, "Caramel Popcorn",
                [new(1, 10, 20, "Siren", 1, 21_500, 99, true),
                 new(1, 11, 21, "Cactuar", 2, 15_000, 99, true),
                 new(1, 12, 22, "Behemoth", 3, 15_100, 99, true)],
                History(22_000, 175, true), HqSalesPerDay: 100m),
        };
        var rules = new List<ProcurementRule> { Rule(1, "Caramel Popcorn", 99, preferred: true) };
        // Eight separate dyes, each a spectacular percentage on a tiny stack.
        for (uint i = 2; i <= 9; i++)
        {
            markets.Add(new(i, $"Dye {i}",
                [new(i, 100 + i, 200 + i, "Siren", 1, 6_600, 20, false),
                 new(i, 110 + i, 210 + i, "Cactuar", 2, 1_148, 20, false)],
                History(6_600, 60, false), NqSalesPerDay: 31m));
            rules.Add(NqRule(i, $"Dye {i}", 20));
        }

        var plan = Plan(markets, rules, budget: 5_000_000);

        var opportunistic = plan.Orders.Where(x => x.Tier == PortfolioTier.Opportunistic).ToArray();
        Assert.Contains(plan.Orders, x => x.ItemId == 1);
        // Capped by slots at ten percent of the portfolio...
        Assert.True(opportunistic.Length <= plan.Summary.OpportunisticCap + plan.Summary.OpportunisticSlots);
        Assert.True(opportunistic.Length <= 6, $"{opportunistic.Length} opportunistic slots taken");
        // ...and holding a trivial share of the capital either way.
        var junkCapital = opportunistic.Sum(x => (decimal)x.CapitalAtRisk);
        Assert.True(junkCapital / plan.TotalCost < 0.10m,
            $"opportunistic stock took {junkCapital:N0} of {plan.TotalCost:N0}");
        Assert.Contains(plan.DecisionLog, x => !x.Selected && x.Reason.Contains("opportunistic"));
    }

    // ---------------------------------------------------------------------
    // 7. A tiny stack must not manufacture a profit rate.
    // ---------------------------------------------------------------------

    [Fact]
    public void TinyStacksDoNotGetAFourTimesVelocityBonus()
    {
        // Twenty units against eighty a day clears in six hours on paper. Under the
        // old quarter-day floor that scored profit x4; the day-long normalisation
        // window reports the profit itself.
        Assert.Equal(1m, PortfolioPolicy.ProfitNormalizationDays);
        Assert.Equal(0.25m, PortfolioPolicy.DaysToSell(20, 80m));
        Assert.Equal(100_000m, PortfolioPolicy.ExpectedGilPerDay(100_000, PortfolioPolicy.DaysToSell(20, 80m)));

        var dye = new ProcurementOrder(2, "Dye", 1, 2, "Cactuar", 2, 1_148, 20, false,
            6_599, 6_599, 101_272, 1, SalesPerDay: 80m, LandedCost: 24_108);
        Assert.Equal(0.25m, dye.EstimatedDaysToSell);
        Assert.Equal(101_272m, dye.ExpectedGilPerDay);

        // And a large slow-turning stack is not penalised into irrelevance either:
        // it reports what it actually earns per day of slot occupancy.
        var popcorn = new ProcurementOrder(1, "Caramel Popcorn", 1, 2, "Cactuar", 2, 15_000, 99, true,
            21_499, 21_499, 462_726, 1, SalesPerDay: 40m, LandedCost: 1_559_250);
        Assert.Equal(2.475m, popcorn.EstimatedDaysToSell);
        Assert.Equal(186_960m, Math.Round(popcorn.ExpectedGilPerDay));
        Assert.True(popcorn.ExpectedGilPerDay > dye.ExpectedGilPerDay);
    }

    // ---------------------------------------------------------------------
    // 8. The resale anchor should reflect depth, not the single cheapest row.
    // ---------------------------------------------------------------------

    [Fact]
    public void ATrivialUndercutDoesNotRedefineAHighVolumeMarket()
    {
        // 150 units a day absorbs 75 in the half-day window.
        ProcurementMarketListing[] shallow =
        [
            new(1, 10, 20, "Siren", 1, 12_000, 3, true),
            new(1, 11, 21, "Siren", 1, 21_500, 20, true),
            new(1, 12, 22, "Siren", 1, 22_000, 99, true),
        ];
        Assert.Equal(22_000u, HomePriceReference.DepthAdjustedLowest(shallow, 150m, 0.5m));

        // Real depth still sets the price: a hundred cheap units is not noise.
        ProcurementMarketListing[] deep =
        [
            new(1, 10, 20, "Siren", 1, 12_000, 100, true),
            new(1, 12, 22, "Siren", 1, 22_000, 99, true),
        ];
        Assert.Equal(12_000u, HomePriceReference.DepthAdjustedLowest(deep, 150m, 0.5m));

        // Without velocity there is nothing to absorb anything, so it stays
        // conservative and takes the cheapest listing.
        Assert.Equal(12_000u, HomePriceReference.DepthAdjustedLowest(shallow, 0m, 0.5m));
        Assert.Equal(12_000u, HomePriceReference.DepthAdjustedLowest(shallow, 150m, 0m));
    }

    [Fact]
    public void ThePlannerPricesAgainstTheDepthAdjustedAnchor()
    {
        var popcorn = new ProcurementMarketItem(1, "Caramel Popcorn",
            [new(1, 10, 20, "Siren", 1, 12_000, 3, true),
             new(1, 11, 21, "Siren", 1, 22_000, 99, true),
             new(1, 12, 22, "Cactuar", 2, 15_000, 99, true)],
            History(22_000, 260, true), HqSalesPerDay: 150m);

        var plan = Plan([popcorn], [Rule(1, "Caramel Popcorn", 99, preferred: true)], 5_000_000);
        // The three-unit undercut is ignored; the anchor is a gil under the real
        // book, for every listing judged against it. Under the old rule the anchor
        // would have been 11,999 and nothing here would have been profitable.
        Assert.NotEmpty(plan.Orders);
        Assert.All(plan.Orders, x => Assert.Equal(21_999u, x.TargetSalePrice));
        Assert.Contains(plan.Orders, x => x.WorldName == "Cactuar" && x.Quantity == 99);
    }

    // ---------------------------------------------------------------------
    // 9. An early cheap windfall must not spend the capital a later one needs.
    // ---------------------------------------------------------------------

    [Fact]
    public void AnEarlyWorldBargainDoesNotConsumeCapitalABetterDealNeeds()
    {
        // Budget fits the gemdraught, or the dye and then nothing.
        var dye = new ProcurementMarketItem(2, "General-Purpose Dye",
            [new(2, 110, 210, "Siren", 1, 6_600, 20, false),
             new(2, 111, 211, "Alpha", 2, 1_148, 20, false)],
            History(6_600, 60, false), NqSalesPerDay: 31m);
        var gemdraught = new ProcurementMarketItem(1, "Grade 4 Gemdraught",
            [new(1, 100, 200, "Siren", 1, 21_500, 99, true),
             new(1, 101, 201, "Zeta", 9, 15_000, 99, true)],
            History(22_000, 70, true), HqSalesPerDay: 40m);

        // The dye is listed first, on the first world of the circuit, at ~420%.
        var plan = Plan([dye, gemdraught],
            [NqRule(2, "General-Purpose Dye", 20), Rule(1, "Grade 4 Gemdraught", 99, preferred: true)],
            budget: 1_570_000);

        var order = Assert.Single(plan.Orders);
        Assert.Equal(1u, order.ItemId);
        Assert.Equal("Zeta", order.WorldName);
        Assert.Contains(plan.DecisionLog,
            x => x.ItemId == 2 && !x.Selected && x.Reason.Contains("budget"));
        // There is no immediate-buy path left for a percentage to trigger.
        Assert.False(ShoppingScoutPolicy.BuysBeforeComparison);
    }

    // ---------------------------------------------------------------------
    // 10. One ROI definition, everywhere.
    // ---------------------------------------------------------------------

    [Fact]
    public void DisplayedPlannedAndLiveValidationRoiAgree()
    {
        var gemdraught = new ProcurementMarketItem(1, "Grade 4 Gemdraught",
            [new(1, 10, 20, "Siren", 1, 21_500, 99, true),
             new(1, 11, 21, "Cactuar", 2, 15_000, 99, true)],
            History(22_000, 70, true), HqSalesPerDay: 40m);

        var plan = Plan([gemdraught], [Rule(1, "Grade 4 Gemdraught", 99, preferred: true)], 5_000_000);
        var order = Assert.Single(plan.Orders);

        // Landed cost, not the raw price, is the denominator everywhere.
        Assert.Equal(Fees.LandedCost(order.PricePerUnit, order.Quantity), order.CapitalAtRisk);
        Assert.Equal(1_559_250ul, order.CapitalAtRisk);
        Assert.Equal(462_726u, order.ExpectedProfit);

        // What the planner ranks on, what the order reports and what the log prints.
        Assert.Equal(order.NetRoiPercent, order.RoiPercent);
        var decision = Assert.Single(plan.DecisionLog, x => x.Selected);
        Assert.Equal(order.NetRoiPercent, decision.RoiPercent);
        Assert.Equal(order.CapitalAtRisk, decision.LandedCost);
        Assert.Equal(order.ExpectedNetProceeds, decision.ExpectedNetProceeds);

        // The arithmetic the live pre-purchase guard performs on the same inputs.
        var liveNetProceeds = Fees.NetProceeds(order.TargetSalePrice, order.Quantity);
        Assert.Equal(order.ExpectedNetProceeds, liveNetProceeds);
        Assert.Equal(order.NetRoiPercent, FeeModel.NetRoiPercent(liveNetProceeds, order.CapitalAtRisk));
        Assert.True(liveNetProceeds >= order.CapitalAtRisk * (1m + order.RequiredRoiPercent / 100m));

        // And it is genuinely the post-fee figure: the old pre-fee basis read higher.
        var preFeeRoi = order.ExpectedProfit * 100m / ((decimal)order.PricePerUnit * order.Quantity);
        Assert.True(preFeeRoi > order.NetRoiPercent);
        Assert.Equal(29.68m, Math.Round(order.NetRoiPercent, 2));
    }

    // ---------------------------------------------------------------------
    // Supporting economics: the margin ladder and the cost basis.
    // ---------------------------------------------------------------------

    [Theory]
    // Liquid and valuable, and pinned: the thinnest bar the bot offers.
    [InlineData(true, 100, 2_000_000ul, 10)]
    // Liquid and valuable but unpinned: a little more margin required.
    [InlineData(false, 100, 2_000_000ul, 14)]
    // Liquid but cheap: stricter, because a fast trinket is not a capital home.
    [InlineData(false, 100, 40_000ul, 35)]
    // Valuable but slow: the ordinary bar.
    [InlineData(false, 4, 2_000_000ul, 20)]
    public void TheMarginBarFollowsVolumeAndValueTogether(
        bool preferred, int salesPerDay, ulong valuePerSlot, int expected) =>
        Assert.Equal(expected, PortfolioPolicy.RequiredRoiPercent(Policy, preferred, salesPerDay, valuePerSlot));

    [Fact]
    public void NothingIsEverBoughtBelowTheAbsoluteFloor()
    {
        var permissive = Policy with
        {
            CoreHighVolumeRoiPercent = 1m, HighVolumeRoiPercent = 1m, StandardRoiPercent = 1m, LowValueRoiPercent = 1m,
        };
        Assert.Equal(Policy.AbsoluteMinimumRoiPercent,
            PortfolioPolicy.RequiredRoiPercent(permissive, true, 500m, 5_000_000));
        // The top-up pass relaxes the good bars and still cannot go under the floor.
        var relaxed = Policy.RelaxedTo(2m);
        Assert.Equal(Policy.AbsoluteMinimumRoiPercent,
            PortfolioPolicy.RequiredRoiPercent(relaxed, true, 500m, 5_000_000));
        // And it does not relax the low-value bar at all.
        Assert.Equal(Policy.LowValueRoiPercent,
            PortfolioPolicy.RequiredRoiPercent(relaxed, false, 500m, 40_000));
    }

    [Fact]
    public void TheCostBasisAveragesAndReleasesInsteadOfRatchetingUp()
    {
        var rule = new PricingRule();
        // One expensive stack: 2,100 gil a unit landed.
        PositionCostPolicy.RecordPurchase(rule, 0, 99, 207_900, 10m, 100, holdingsKnown: true);
        Assert.Equal(2_100u, rule.CostBasis);
        Assert.Equal(99u, rule.CostBasisUnits);
        var expensiveFloor = rule.AcquisitionFloor;

        // A cheaper stack pulls the basis down instead of being stranded above it.
        PositionCostPolicy.RecordPurchase(rule, 99, 99, 108_900, 10m, 100, holdingsKnown: true);
        Assert.Equal(1_600u, rule.CostBasis);
        Assert.Equal(198u, rule.CostBasisUnits);
        Assert.True(rule.AcquisitionFloor < expensiveFloor);

        // Selling the position out clears the basis, so the next buy starts fresh.
        PositionCostPolicy.RecordSale(rule, 198);
        Assert.Equal(0u, rule.CostBasisUnits);
        Assert.Equal(0u, rule.CostBasis);
        Assert.Equal(0u, rule.AcquisitionFloor);

        // With holdings unverifiable the protective high-water mark is kept.
        var guarded = new PricingRule { CostBasis = 2_100, CostBasisUnits = 99 };
        PositionCostPolicy.RecordPurchase(guarded, 99, 99, 108_900, 10m, 100, holdingsKnown: false);
        Assert.Equal(2_100u, guarded.CostBasis);
    }

    [Fact]
    public void TheResaleFloorGrossesUpThroughTheObservedTax()
    {
        // 1,680 gil landed, a 10% bar, and a sale tax that has to be paid on top.
        Assert.Equal(1_946u, ProcurementPriceSafety.MinimumResalePrice(1_680, 10m, 100));
        // A different reported tax moves the floor, and the same model is used.
        var lighter = new FeeModel(3m, 5m);
        Assert.Equal(1_906u, ProcurementPriceSafety.MinimumResalePrice(1_680, 10m, 100, lighter));
    }

    [Fact]
    public void DiscoveryProposalsAreCandidatesUntilTheEvidenceSaysOtherwise()
    {
        var strong = new MarketConfidenceEvidence(
            SalesPerDay: 120m, StackValue: 2_000_000, ExpectedGilPerDay: 160_000m,
            Confirmations: 5, PriceSpreadPercent: 4m, HasProfitableSpread: true);
        Assert.Equal(MarketConfidence.Proven, MarketConfidencePolicy.Classify(strong));
        Assert.True(MarketConfidencePolicy.ShouldPin(MarketConfidence.Proven));

        // One flattering observation is not evidence of a market.
        Assert.Equal(MarketConfidence.Candidate,
            MarketConfidencePolicy.Classify(strong with { Confirmations = 1 }));
        // Nor is a market whose price will not sit still.
        Assert.Equal(MarketConfidence.Candidate,
            MarketConfidencePolicy.Classify(strong with { PriceSpreadPercent = 60m }));
        // Nor one whose spread has never actually been realised.
        Assert.Equal(MarketConfidence.Candidate,
            MarketConfidencePolicy.Classify(strong with { HasProfitableSpread = false }));
        // Cheap or quiet lines never leave the bottom rung.
        Assert.Equal(MarketConfidence.Opportunistic,
            MarketConfidencePolicy.Classify(strong with { StackValue = 40_000 }));
        Assert.Equal(MarketConfidence.Opportunistic,
            MarketConfidencePolicy.Classify(strong with { SalesPerDay = 5m }));
        Assert.False(MarketConfidencePolicy.ShouldPin(MarketConfidence.Candidate));
    }
}
