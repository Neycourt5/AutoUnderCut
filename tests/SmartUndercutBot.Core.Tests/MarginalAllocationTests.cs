using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// The marginal-inventory model and the constrained-capital repair pass.
///
/// The question these ask is not "is this a good deal" - the margin ladder already
/// answered that - but "what is one more stack worth given what will already be
/// sitting in front of it", and "did greedy leave gil on the table".
/// </summary>
public sealed class MarginalAllocationTests(ITestOutputHelper output)
{
    private readonly ProcurementPlannerService planner = new();

    private static ProcurementEconomicPolicy Policy => ProcurementEconomicPolicy.Default;
    private static PortfolioGates Gates => new(75m, 10m, 2_500);

    private static IReadOnlyList<ProcurementSale> History(uint price, uint quantity, bool hq)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(1, 4)
            .Select(i => new ProcurementSale(price, quantity, hq, now.AddDays(-i * 0.5)))
            .ToArray();
    }

    private static ProcurementRule Rule(uint id, string name, int stack, bool preferred, int slots = 8) => new()
    {
        ItemId = id, ItemName = name, TargetStackSize = stack, MaximumSaleSlots = slots,
        MinimumWeeklyUnitsSold = 20, PreferredStock = preferred,
        AllowHighQuality = true, RequireHighQuality = true,
    };

    /// <summary>A market with a home anchor listing plus <paramref name="offers"/> away listings.</summary>
    private static ProcurementMarketItem Market(
        uint id, string name, uint homePrice, decimal salesPerDay, uint historyPrice, uint historyQuantity,
        params (uint Price, uint Quantity, string World, ulong ListingId)[] offers) =>
        new(id, name,
            [new(id, 1, 1, "Siren", 1, homePrice, 99, true),
             .. offers.Select(o => new ProcurementMarketListing(id, o.ListingId, o.ListingId + 500, o.World, 2, o.Price, o.Quantity, true))],
            History(historyPrice, historyQuantity, true), HqSalesPerDay: salesPerDay);

    private ProcurementPlan Plan(
        IReadOnlyList<ProcurementMarketItem> markets, IReadOnlyList<ProcurementRule> rules,
        uint budget, int saleSlots = 20, IReadOnlyList<StockExposure>? owned = null) =>
        planner.BuildPlan(new(markets, rules, budget, saleSlots, 60, 20m, 100,
            HomeWorld: "Siren", OwnedStock: owned, MaximumWeeklySalesSharePercent: 25m,
            Portfolio: Gates, PortfolioCapacitySlots: 60, Economics: Policy));

    private static ProcurementMarketItem Popcorn(int stacks, uint price = 15_000) =>
        Market(1, "Caramel Popcorn", 21_500, 100m, 22_000, 175,
            [.. Enumerable.Range(0, stacks).Select(i => (price, 99u, $"World{i}", (ulong)(100 + i)))]);

    [Fact]
    public void DuplicateMarketRowsCannotBuyTheSameListingTwiceAcrossWorldCasing()
    {
        var market = Popcorn(1);
        var duplicate = market with
        {
            Listings = market.Listings.Select(x => x with { WorldName = x.WorldName.ToUpperInvariant() }).ToArray(),
        };
        var plan = Plan([market, duplicate], [Rule(1, "Popcorn", 99, true)], 15_000_000);
        Assert.Single(plan.Orders);
    }

    [Fact]
    public void ListingIdentityKeepsSeparateQualitiesWithSyntheticIds()
    {
        var hq = Popcorn(1);
        var nq = hq with
        {
            Listings = hq.Listings.Select(x => x with { IsHighQuality = false }).ToArray(),
            RecentSales = hq.RecentSales.Select(x => x with { IsHighQuality = false }).ToArray(),
            NqSalesPerDay = 100m,
        };
        var rule = Rule(1, "Popcorn", 99, true);
        rule.RequireHighQuality = false;
        var first = Plan([hq, nq], [rule], 15_000_000);
        var reversed = Plan([nq, hq], [rule], 15_000_000);
        Assert.Equal(2, first.Orders.Count);
        Assert.Equal([false, true], first.Orders.Select(x => x.IsHighQuality).ToArray());
        Assert.Equal(first.Orders, reversed.Orders);
    }

    [Fact]
    public void RepeatedMarketsUseTheTighterWeeklyShareRegardlessOfInputOrder()
    {
        var low = Popcorn(3) with { RecentSales = History(22_000, 100, true) };
        var high = Popcorn(3) with { RecentSales = History(22_000, 1_000, true) };
        ProcurementPlan Build(ProcurementMarketItem[] markets) => planner.BuildPlan(new(
            markets, [Rule(1, "Popcorn", 99, true)], 15_000_000, 20, 60, 20m, 100,
            HomeWorld: "Siren", OwnedStock: [new(1, true, 1, 1, PortfolioTier.Core)],
            MaximumWeeklySalesSharePercent: 25m)); // Flat policy: weekly cap, no coverage sizing.
        var first = Build([low, high]);
        Assert.Single(first.Orders); // floor(400 * 25%) - 1 owned = 99 units left.
        Assert.Equal(first.Orders, Build([high, low]).Orders);
    }

    [Fact]
    public void PartialStackFitsRemainingCoverageAndScoresBehindExistingHoldings()
    {
        var market = Market(1, "Popcorn", 21_500, 100m, 22_000, 175,
            (15_000, 99, "Full", 101), (15_000, 20, "Partial", 102));
        var plan = Plan([market], [Rule(1, "Popcorn", 99, true)], 15_000_000,
            owned: [new(1, true, 290, 3, PortfolioTier.Core)]);
        Assert.Equal(99u, Assert.Single(plan.Orders).Quantity);
        // A full stack would exceed the ceiling at lower velocity; the partial remains feasible.
        plan = Plan([market with { HqSalesPerDay = 80m }], [Rule(1, "Popcorn", 99, true)], 15_000_000,
            owned: [new(1, true, 230, 3, PortfolioTier.Core)]);
        var partial = Assert.Single(plan.Orders);
        Assert.Equal(20u, partial.Quantity);
        Assert.Equal(230UL, partial.OwnedUnitsBefore);
        Assert.Equal(2.875m, partial.InventoryCoverageDays);
        Assert.Equal(3.125m, partial.CoverageDaysAfterPurchase);
        Assert.Equal(37_392m, partial.AllocationScore);
    }

    [Fact]
    public void MarginalNormalizationHasNoJumpAtOneDay()
    {
        Assert.Equal(100_000m, PortfolioPolicy.MarginalGilPerDay(100_000, 79, 20, 100m));
        Assert.Equal(100_000m, PortfolioPolicy.MarginalGilPerDay(100_000, 80, 20, 100m));
        Assert.Equal(100_000m / 1.01m, PortfolioPolicy.MarginalGilPerDay(100_000, 81, 20, 100m));
    }

    [Fact]
    public void FifteenMillionWalkthroughMatchesTheDocumentedBasket()
    {
        var markets = new[]
        {
            Popcorn(6),
            Market(2, "Popoto Potage", 21_500, 120m, 22_000, 210,
                (17_600, 99, "Cactuar", 201), (17_600, 99, "Cactuar", 202)),
            Market(3, "Gemdraught of Water", 21_500, 40m, 22_000, 70,
                (15_000, 99, "Cactuar", 301), (15_000, 99, "Cactuar", 302)),
            Market(4, "Jhinga Curry", 17_000, 60m, 17_500, 105, (12_000, 99, "Cactuar", 401)),
            Market(5, "Gemdraught of Fire", 21_500, 40m, 22_000, 70, (16_000, 99, "Cactuar", 501)),
            Market(6, "Grade XI Materia", 14_001, 25m, 14_500, 44,
                (8_000, 20, "Cactuar", 601), (8_000, 20, "Cactuar", 602), (8_000, 20, "Cactuar", 603)),
            Market(7, "General-Purpose Dye", 6_600, 31m, 6_600, 60, (1_148, 20, "Cactuar", 701)),
            Market(8, "Gemdraught of Earth", 21_500, 40m, 22_000, 70, (19_000, 99, "Cactuar", 801)),
        };
        // Materia and dye have no HQ form.
        foreach (var i in new[] { 5, 6 })
            markets[i] = markets[i] with
            {
                Listings = markets[i].Listings.Select(x => x with { IsHighQuality = false }).ToArray(),
                RecentSales = markets[i].RecentSales.Select(x => x with { IsHighQuality = false }).ToArray(),
                NqSalesPerDay = markets[i].HqSalesPerDay, HqSalesPerDay = null,
            };
        var rules = markets.Select(m => Rule(m.ItemId, m.ItemName, m.ItemId is 6 or 7 ? 20 : 99,
            m.ItemId is 1 or 2 or 3 or 5 or 8)).ToArray();
        foreach (var rule in rules.Where(r => r.ItemId is 6 or 7))
            rule.AllowHighQuality = rule.RequireHighQuality = false;
        var plan = Plan(markets, rules, 15_000_000, owned: [new(1, true, 80, 1, PortfolioTier.Core)]);
        Assert.Equal([1u, 2u, 3u, 4u, 1u, 5u, 1u, 2u, 6u, 6u, 7u], plan.Orders.Select(x => x.ItemId).ToArray());
        Assert.Equal(13_166_748u, plan.TotalCost);
        Assert.Equal(3_243_215u, plan.ExpectedProfit);
        ulong running = 0;
        foreach (var order in plan.Orders)
        {
            running += order.CapitalAtRisk;
            output.WriteLine(FormattableString.Invariant(
                $"| {order.ItemName} | {order.PricePerUnit:N0} | {order.CapitalAtRisk:N0} | {order.MarginalGilPerDay:N2} | {order.AllocationScore:N2} | {order.InventoryCoverageDays:N3} | {order.CoverageDaysAfterPurchase:N3} | {order.ExpectedProfit:N0} | {running:N0} |"));
        }
        output.WriteLine($"Marginal objective: {plan.Orders.Sum(x => x.AllocationScore):N2}");
        // Input enumeration cannot change the basket, its order or its marginal scores.
        Assert.Equal(plan.Orders, Plan(markets.Reverse().Select(m => m with
        { Listings = m.Listings.Reverse().ToArray() }).ToArray(), rules.Reverse().ToArray(),
            15_000_000, owned: [new(1, true, 80, 1, PortfolioTier.Core)]).Orders);
    }

    [Theory]
    [InlineData(10, 60, 0, true)]
    [InlineData(10, 60, 5, false)] // Only one opportunistic slot remains.
    [InlineData(3, 1_000, 0, false)] // Slots remain, but 30k capital cannot buy both dyes.
    public void RepairRebuildsOpportunisticCapsAgainstTheReplacementBasket(
        int percent, int capacity, int ownedSlots, bool improves)
    {
        var bulk = Market(1, "Bulk", 12_301, 40m, 12_500, 70, (9_600, 99, "Cactuar", 11));
        var dyes = new[] { 2u, 3u }.Select(id => new ProcurementMarketItem(id, $"Dye {id}",
            [new(id, 1, 1, "Siren", 1, 6_600, 20, false),
             new(id, id * 10, id * 10 + 1, "Cactuar", 2, 1_148, 20, false)],
            History(6_600, 60, false), NqSalesPerDay: 31m)).ToArray();
        var rules = new[] { Rule(1, "Bulk", 99, false),
            new ProcurementRule { ItemId = 2, TargetStackSize = 20 },
            new ProcurementRule { ItemId = 3, TargetStackSize = 20 } };
        var plan = planner.BuildPlan(new([bulk, .. dyes], rules, 1_000_000, 4, 60, 20m, 100,
            HomeWorld: "Siren", Economics: Policy, Portfolio: new(75m, percent, 2_500),
            PortfolioCapacitySlots: capacity,
            OwnedStock: ownedSlots > 0 ? [new(999, false, 100, ownedSlots, PortfolioTier.Opportunistic)] : []));
        Assert.Equal(improves ? new[] { 2u, 3u } : [1u], plan.Orders.Select(x => x.ItemId).ToArray());
        Assert.True(plan.Orders.Where(x => x.Tier == PortfolioTier.Opportunistic)
            .Sum(x => (decimal)x.CapitalAtRisk) <= 1_000_000m * percent / 100m);
        Assert.True(plan.Summary.OpportunisticSlots <= plan.Summary.OpportunisticCap);
    }

    // ------------------------------------------------------------------
    // The marginal model itself.
    // ------------------------------------------------------------------

    [Fact]
    public void MarginalClearingCountsTheInventoryQueuedInFront()
    {
        // Nothing held: identical to the standalone figure, so a fresh market is
        // scored exactly as it always was.
        Assert.Equal(PortfolioPolicy.DaysToSell(99, 100m), PortfolioPolicy.MarginalDaysToClear(0, 99, 100m));

        // 179 units against 100 a day is 1.79 days before the new stack is gone.
        Assert.Equal(1.79m, Math.Round(PortfolioPolicy.MarginalDaysToClear(80, 99, 100m), 2));
        Assert.Equal(2.78m, Math.Round(PortfolioPolicy.MarginalDaysToClear(179, 99, 100m), 2));

        // The one-day normalisation still applies, and applies to the marginal
        // figure: a stack that would clear in two hours cannot claim twelve times
        // the rate just for being small.
        // The reported figure keeps the same 0.25-day lower clamp as DaysToSell; the
        // scoring denominator is the separate one-day normalisation below.
        Assert.Equal(PortfolioPolicy.MinimumDaysToSell, PortfolioPolicy.MarginalDaysToClear(0, 20, 500m));
        Assert.Equal(100_000m, PortfolioPolicy.MarginalGilPerDay(100_000, 0, 20, 500m));
        // ...but the same stack landing behind 1,480 units of a 500/day market waits
        // three days to clear, and is measured over those three days instead.
        Assert.Equal(3m, PortfolioPolicy.MarginalDaysToClear(1_480, 20, 500m));
        Assert.Equal(33_333m, Math.Round(PortfolioPolicy.MarginalGilPerDay(100_000, 1_480, 20, 500m)));

        // No velocity means no basis for a clearing estimate; the maximum applies.
        Assert.Equal(PortfolioPolicy.MaximumDaysToSell, PortfolioPolicy.MarginalDaysToClear(0, 99, 0m));
        Assert.Equal(PortfolioPolicy.MaximumDaysToSell, PortfolioPolicy.MarginalDaysToClear(0, 0, 100m));
    }

    // ------------------------------------------------------------------
    // A. An excellent Core market can still take several stacks.
    // ------------------------------------------------------------------

    [Fact]
    public void AnExcellentCoreMarketStillReceivesMultipleStacks()
    {
        var plan = Plan([Popcorn(6)], [Rule(1, "Caramel Popcorn", 99, true, slots: 2)],
            budget: 15_000_000, owned: [new(1, true, 80, 1, PortfolioTier.Core)]);

        Assert.Equal(3, plan.Orders.Count);
        Assert.All(plan.Orders, x => Assert.Equal(1u, x.ItemId));
        Assert.True(plan.TotalCost > 4_000_000, $"deployed only {plan.TotalCost:N0}");
        // 80 -> 179 -> 278 -> 377 units against a 300-unit target and a 400 ceiling.
        Assert.Equal(377m / 100m, plan.Orders[^1].CoverageDaysAfterPurchase);
    }

    // ------------------------------------------------------------------
    // B. Later stacks of the same item are worth strictly less.
    // ------------------------------------------------------------------

    [Fact]
    public void EachAdditionalStackOfTheSameItemScoresStrictlyLower()
    {
        var plan = Plan([Popcorn(6)], [Rule(1, "Caramel Popcorn", 99, true)], budget: 15_000_000);

        Assert.True(plan.Orders.Count >= 3);
        // Identical listings, identical profit - and yet a decaying score, because
        // each one clears further behind the last.
        Assert.All(plan.Orders, x => Assert.Equal(462_726u, x.ExpectedProfit));
        foreach (var pair in plan.Orders.Zip(plan.Orders.Skip(1)))
        {
            Assert.True(pair.Second.AllocationScore < pair.First.AllocationScore,
                $"{pair.Second.AllocationScore:N0} should be under {pair.First.AllocationScore:N0}");
            Assert.True(pair.Second.MarginalDaysToClear > pair.First.MarginalDaysToClear);
        }
        // The standalone figure is what used to be ranked on, and it does not move.
        Assert.Single(plan.Orders.Select(x => x.ExpectedGilPerDay).Distinct());
    }

    // ------------------------------------------------------------------
    // C. Strong alternatives interleave instead of queueing behind Popcorn.
    // ------------------------------------------------------------------

    [Fact]
    public void StrongAlternativesInterleaveWithAFillingCoreMarket()
    {
        var markets = new[]
        {
            Popcorn(3),
            Market(2, "Popoto Potage", 21_500, 120m, 22_000, 210, (17_600, 99, "Cactuar", 201)),
            Market(3, "Gemdraught of Water", 21_500, 40m, 22_000, 70, (15_000, 99, "Cactuar", 301)),
            Market(4, "Jhinga Curry", 17_000, 60m, 17_500, 105, (12_000, 99, "Cactuar", 401)),
        };
        var rules = new[]
        {
            Rule(1, "Caramel Popcorn", 99, true), Rule(2, "Popoto Potage", 99, true),
            Rule(3, "Gemdraught of Water", 99, true), Rule(4, "Jhinga Curry", 99, false),
        };

        var plan = Plan(markets, rules, budget: 15_000_000, owned: [new(1, true, 80, 1, PortfolioTier.Core)]);
        var order = plan.Orders.Select(x => x.ItemId).ToArray();

        // Popcorn opens - it is the single best use of the next gil - and then other
        // markets overtake it before its second stack.
        Assert.Equal(1u, order[0]);
        var firstPopcorn = Array.IndexOf(order, 1u);
        var lastPopcorn = Array.LastIndexOf(order, 1u);
        Assert.True(lastPopcorn - firstPopcorn > order.Count(x => x == 1u) - 1,
            $"popcorn was taken as one uninterrupted block: {string.Join(",", order)}");
        // Specifically: something else is bought before the second popcorn stack.
        Assert.NotEqual(1u, order[1]);
        Assert.Equal(3, order.Count(x => x == 1u));
    }

    // ------------------------------------------------------------------
    // D. Dominance still wins: weak alternatives do not earn a turn.
    // ------------------------------------------------------------------

    [Fact]
    public void ADominantCoreMarketStillFillsDeeplyWhenEverythingElseIsWorse()
    {
        var markets = new[]
        {
            Popcorn(4),
            // Profitable, buyable and a whole stack fits inside its coverage - just
            // far less productive per day of committed capital.
            Market(9, "Mediocre Stock", 21_500, 40m, 22_000, 70, (17_000, 99, "Cactuar", 901)),
        };
        var rules = new[] { Rule(1, "Caramel Popcorn", 99, true), Rule(9, "Mediocre Stock", 99, false) };

        var plan = Plan(markets, rules, budget: 15_000_000, saleSlots: 5);
        var order = plan.Orders.Select(x => x.ItemId).ToArray();

        Assert.True(order.Length >= 4);
        // Four consecutive popcorn stacks, and only then the alternative. Nothing
        // forces interleaving when interleaving is the worse trade.
        Assert.Equal([1u, 1u, 1u, 1u], order.Take(4).ToArray());
        Assert.Contains(9u, order);
    }

    // ------------------------------------------------------------------
    // Constrained capital: greedy leaves gil on the table, the repair finds it.
    // ------------------------------------------------------------------

    private static (ProcurementMarketItem[] Markets, ProcurementRule[] Rules) CrowdingOutFixture() =>
    ([
        // One expensive stack: 997,920 gil, 742,005 profit, 2.48 days -> ~299,800/day.
        Market(1, "Bulk Elixir", 18_501, 40m, 19_000, 70, (9_600, 99, "Cactuar", 11)),
        // Two cheaper ones: 488,565 gil each, 357,885 profit, 1.8 days -> ~198,825/day.
        Market(2, "Twin Tonic A", 9_001, 55m, 9_200, 96, (4_700, 99, "Cactuar", 21)),
        Market(3, "Twin Tonic B", 9_001, 55m, 9_200, 96, (4_700, 99, "Cactuar", 31)),
    ],
    [
        Rule(1, "Bulk Elixir", 99, false), Rule(2, "Twin Tonic A", 99, false), Rule(3, "Twin Tonic B", 99, false),
    ]);

    [Fact]
    public void TightCapitalPrefersTwoCheaperStacksOverOneExpensiveOne()
    {
        var (markets, rules) = CrowdingOutFixture();

        // Naive greedy takes Bulk Elixir at 299,800/day and then cannot afford
        // anything else. Two Twin Tonics are together worth 397,650/day for less gil.
        var plan = Plan(markets, rules, budget: 1_000_000, saleSlots: 4);

        Assert.Equal(2, plan.Orders.Count);
        Assert.Equal([2u, 3u], plan.Orders.Select(x => x.ItemId).Order().ToArray());
        Assert.DoesNotContain(plan.Orders, x => x.ItemId == 1u);
        Assert.Equal(977_130u, plan.TotalCost);
        Assert.True(plan.TotalCost <= 1_000_000);
        Assert.Equal(397_650m, plan.Orders.Sum(x => x.AllocationScore));
        // Note the trade: less absolute profit, but the capital comes back sooner and
        // goes back to work. That is the objective, and it is stated plainly.
        Assert.Equal(715_770u, plan.ExpectedProfit);
    }

    [Fact]
    public void TheRepairPassRespectsTheSlotConstraintItCannotBuyAround()
    {
        var (markets, rules) = CrowdingOutFixture();

        // Same gil, but only one slot. Swapping one stack for two is not available,
        // so the expensive stack correctly keeps the slot.
        var plan = Plan(markets, rules, budget: 1_000_000, saleSlots: 1);

        var order = Assert.Single(plan.Orders);
        Assert.Equal(1u, order.ItemId);
        Assert.Equal(997_920u, plan.TotalCost);
    }

    [Fact]
    public void ALargeWalletIsUnaffectedByTheRepairPass()
    {
        var (markets, rules) = CrowdingOutFixture();

        var plan = Plan(markets, rules, budget: 15_000_000, saleSlots: 4);

        // Nothing was crowded out, so nothing is swapped: all three are bought, best
        // first, exactly as plain greedy would have.
        Assert.Equal(3, plan.Orders.Count);
        Assert.Equal([1u, 2u, 3u], plan.Orders.Select(x => x.ItemId).ToArray());
        Assert.Equal(1_975_050u, plan.TotalCost);
    }

    [Fact]
    public void AllocationIsDeterministic()
    {
        var (markets, rules) = CrowdingOutFixture();
        var withPopcorn = markets.Append(Popcorn(3)).ToArray();
        var withRules = rules.Append(Rule(1_000, "Caramel Popcorn", 99, true)).ToArray();
        // Reuse the popcorn fixture under a distinct id so both compete.
        withPopcorn[^1] = withPopcorn[^1] with { ItemId = 1_000 };
        withPopcorn[^1] = withPopcorn[^1] with
        {
            Listings = withPopcorn[^1].Listings.Select(x => x with { ItemId = 1_000 }).ToArray(),
        };

        var first = Plan(withPopcorn, withRules, budget: 3_000_000, saleSlots: 6);
        for (var i = 0; i < 5; i++)
        {
            var again = Plan(withPopcorn, withRules, budget: 3_000_000, saleSlots: 6);
            Assert.Equal(
                first.Orders.Select(x => (x.ItemId, x.ListingId, x.WorldName)).ToArray(),
                again.Orders.Select(x => (x.ItemId, x.ListingId, x.WorldName)).ToArray());
            Assert.Equal(first.TotalCost, again.TotalCost);
        }
    }

    // ------------------------------------------------------------------
    // Adversarial cases.
    // ------------------------------------------------------------------

    [Fact]
    public void ManyListingsOfOneItemCannotOutrunItsOwnDemand()
    {
        // Twenty stacks on offer of the single best market in the game. Coverage,
        // not enthusiasm, decides how many are bought.
        var plan = Plan([Popcorn(20)], [Rule(1, "Caramel Popcorn", 99, true)],
            budget: 100_000_000, saleSlots: 60);

        Assert.Equal(4, plan.Orders.Count);
        Assert.True(plan.Orders[^1].CoverageDaysAfterPurchase <= 4m);
        Assert.Contains(plan.DecisionLog, x => !x.Selected && x.Reason.Contains("demand coverage reached"));
    }

    [Fact]
    public void HoldingsAlreadyNearTargetAdmitOneStackAndNoMore()
    {
        var plan = Plan([Popcorn(6)], [Rule(1, "Caramel Popcorn", 99, true)],
            budget: 100_000_000, owned: [new(1, true, 290, 3, PortfolioTier.Core)]);

        Assert.Single(plan.Orders);
        Assert.Equal(3.89m, Math.Round(plan.Orders[0].CoverageDaysAfterPurchase, 2));
    }

    [Fact]
    public void WidePriceVariationWithinOneItemIsTakenCheapestFirst()
    {
        var market = Market(1, "Caramel Popcorn", 21_500, 100m, 22_000, 175,
            (17_000, 99, "Expensive", 103), (15_000, 99, "Cheap", 101), (16_000, 99, "Middle", 102));

        var plan = Plan([market], [Rule(1, "Caramel Popcorn", 99, true)], budget: 15_000_000);

        Assert.Equal([15_000u, 16_000u, 17_000u], plan.Orders.Select(x => x.PricePerUnit).ToArray());
        // Marginal decay and rising price both push the same way here, so the scores
        // fall faster than the queue position alone would explain.
        Assert.True(plan.Orders[1].AllocationScore < plan.Orders[0].AllocationScore / 2m);
    }

    [Fact]
    public void APartialStackDoesNotOutrankAFullOneForAScarceSlot()
    {
        var market = Market(1, "Caramel Popcorn", 21_500, 100m, 22_000, 175,
            (15_000, 20, "Partial", 101), (15_000, 99, "Full", 102));

        var plan = Plan([market], [Rule(1, "Caramel Popcorn", 99, true)], budget: 15_000_000, saleSlots: 1);

        var order = Assert.Single(plan.Orders);
        Assert.Equal(99u, order.Quantity);
        // The partial lot clears in a fifth of a day on paper, is still normalised to
        // a full day, and so is scored at its own profit - which is a fifth of the
        // full stack's. A whole sale slot for a fifth of the gil is the worse trade.
        Assert.Equal(PortfolioPolicy.MinimumDaysToSell, PortfolioPolicy.MarginalDaysToClear(0, 20, 100m));
        Assert.Equal(93_480m, PortfolioPolicy.MarginalGilPerDay(93_480, 0, 20, 100m));
        Assert.Equal(462_726m, PortfolioPolicy.MarginalGilPerDay(462_726, 0, 99, 100m));
    }

    [Fact]
    public void ASpectacularDyeStillCannotBuyItsWayIntoAScarceSlot()
    {
        var markets = new[]
        {
            Popcorn(2),
            new ProcurementMarketItem(2, "General-Purpose Dye",
                [new(2, 1, 1, "Siren", 1, 6_600, 20, false),
                 new(2, 21, 31, "Cactuar", 2, 1_148, 20, false)],
                History(6_600, 60, false), NqSalesPerDay: 31m),
        };
        var rules = new[]
        {
            Rule(1, "Caramel Popcorn", 99, true),
            new ProcurementRule { ItemId = 2, ItemName = "General-Purpose Dye", TargetStackSize = 20, MaximumSaleSlots = 2, MinimumWeeklyUnitsSold = 20 },
        };

        var plan = Plan(markets, rules, budget: 15_000_000, saleSlots: 2);

        Assert.Equal(2, plan.Orders.Count);
        Assert.All(plan.Orders, x => Assert.Equal(1u, x.ItemId));
        var dye = Assert.Single(plan.DecisionLog, x => x.ItemId == 2 && !x.Selected);
        Assert.True(dye.RoiPercent > 400m);
    }

    [Fact]
    public void NoUsableVelocityMeansNoPositionToSize()
    {
        // Live-tour planning has no demand gate, so this is the path where a market
        // with no sales history at all can still reach allocation.
        var market = new ProcurementMarketItem(1, "Mystery Stock",
            [new(1, 1, 1, "Siren", 1, 20_000, 99, true),
             new(1, 11, 21, "Cactuar", 2, 10_000, 99, true)], []);
        var plan = planner.BuildLiveMarketPlan(new([market], [Rule(1, "Mystery Stock", 99, false)],
            "Siren", new HashSet<ulong>(), 15_000_000, 10, 60, 20m, 100,
            Portfolio: Gates, PortfolioCapacitySlots: 60, Economics: Policy));

        Assert.Empty(plan.Orders);
        Assert.Contains(plan.DecisionLog,
            x => !x.Selected && x.Reason.Contains("no reliable sales velocity"));
    }

    [Fact]
    public void ATightBudgetNeverExceedsItselfHoweverManyRepairRoundsRun()
    {
        var markets = new[]
        {
            Popcorn(4),
            Market(2, "Popoto Potage", 21_500, 120m, 22_000, 210,
                (17_600, 99, "A", 201), (17_600, 99, "B", 202)),
            Market(3, "Jhinga Curry", 17_000, 60m, 17_500, 105,
                (12_000, 99, "A", 301), (12_000, 99, "B", 302)),
        };
        var rules = new[]
        {
            Rule(1, "Caramel Popcorn", 99, true), Rule(2, "Popoto Potage", 99, true), Rule(3, "Jhinga Curry", 99, false),
        };

        foreach (var budget in new uint[] { 500_000, 1_300_000, 2_000_000, 3_500_000, 5_000_000, 9_000_000 })
        {
            var plan = Plan(markets, rules, budget, saleSlots: 8);
            Assert.True(plan.TotalCost <= budget, $"budget {budget:N0} overspent to {plan.TotalCost:N0}");
            Assert.True(plan.Orders.Count <= 8);
            Assert.Equal(plan.TotalCost, plan.Orders.Aggregate(0UL, (sum, o) => sum + o.CapitalAtRisk));
            Assert.Equal(plan.ExpectedProfit, plan.Orders.Aggregate(0UL, (sum, o) => sum + o.ExpectedProfit));
            // Coverage is never breached by the repair pass either.
            foreach (var group in plan.Orders.GroupBy(x => x.ItemId))
                Assert.True(group.Last().CoverageDaysAfterPurchase <=
                    Policy.CoverageDaysFor(group.Last().Tier) + Policy.CoverageOvershootDays + 0.01m);
        }
    }

    [Fact]
    public void TheRepairPassIsNotDefeatedByIdenticalSiblingListings()
    {
        // The trap: a market with two interchangeable listings. Banning the one that
        // greedy picked achieves nothing, because its twin simply takes the slot. The
        // repair therefore works in per-market slot caps, not banned listings.
        var markets = new[]
        {
            Popcorn(3),
            // Expensive and thin: 1,829,520 gil for 192,456 profit. Greedy takes it
            // second on rate alone, and it then blocks two better stacks.
            Market(2, "Popoto Potage", 21_500, 120m, 22_000, 210,
                (17_600, 99, "A", 201), (17_600, 99, "B", 202)),
            Market(3, "Gemdraught of Water", 21_500, 40m, 22_000, 70, (15_000, 99, "Cactuar", 301)),
            Market(4, "Jhinga Curry", 17_000, 60m, 17_500, 105, (12_000, 99, "Cactuar", 401)),
        };
        var rules = new[]
        {
            Rule(1, "Caramel Popcorn", 99, true), Rule(2, "Popoto Potage", 99, true),
            Rule(3, "Gemdraught of Water", 99, true), Rule(4, "Jhinga Curry", 99, false),
        };

        var plan = Plan(markets, rules, budget: 6_000_000, saleSlots: 20,
            owned: [new(1, true, 80, 1, PortfolioTier.Core)]);

        // Popoto is dropped entirely; the gil buys Water, Jhinga and a second Popcorn.
        Assert.DoesNotContain(plan.Orders, x => x.ItemId == 2u);
        Assert.Equal([1u, 3u, 4u, 1u], plan.Orders.Select(x => x.ItemId).ToArray());
        Assert.True(plan.TotalCost <= 6_000_000);
        // Strictly better on the objective, and on absolute profit too.
        Assert.Equal(5_925_150u, plan.TotalCost);
        Assert.Equal(977_833m, Math.Round(plan.Orders.Sum(x => x.AllocationScore)));
        Assert.Equal(1_739_529u, plan.ExpectedProfit);
        // Plain greedy would have stopped at Popcorn, Popoto and Water for
        // 4,948,020 gil and an objective of 797,403 - fewer stacks, less profit and
        // 23% less gil per day, with a million gil left idle it could not use.
        Assert.True(plan.Orders.Sum(x => x.AllocationScore) > 797_403m);
        Assert.Contains(plan.DecisionLog,
            x => x.ItemId == 2u && !x.Selected && x.Reason.Contains("cheaper stock"));
    }

    [Fact]
    public void MoreSlotsNeverProduceAWorseBasket()
    {
        var markets = new[]
        {
            Popcorn(4),
            Market(2, "Popoto Potage", 21_500, 120m, 22_000, 210, (17_600, 99, "A", 201)),
            Market(3, "Jhinga Curry", 17_000, 60m, 17_500, 105, (12_000, 99, "A", 301)),
        };
        var rules = new[]
        {
            Rule(1, "Caramel Popcorn", 99, true), Rule(2, "Popoto Potage", 99, true), Rule(3, "Jhinga Curry", 99, false),
        };

        decimal previous = 0;
        for (var slots = 1; slots <= 6; slots++)
        {
            var objective = Plan(markets, rules, 15_000_000, saleSlots: slots).Orders.Sum(x => x.AllocationScore);
            Assert.True(objective >= previous, $"{slots} slots scored {objective:N0} under {previous:N0}");
            previous = objective;
        }
    }
}
