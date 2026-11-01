using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// Decisions 3, 4 and 6 of the 2026-11-01 review: holdings sized on the share of
/// the market our listings actually win, food and potions on their own margin bar
/// with a profit floor every lot must clear, and listings sized to their buyers.
/// </summary>
public sealed class CaptureShareAndListingSizeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly ProcurementPlannerService planner = new();

    [Fact]
    public void NoShareIsMeasuredBeforeTwelveHoursOfListening()
    {
        var watched = new[] { new WatchedSpan(Now.AddHours(-6), Now) };
        Assert.Null(CaptureShareEstimator.Measure(Sales(10, 50, Now.AddHours(-1)), watched, 1, true, 1_950m, Now));
    }

    [Fact]
    public void NoShareIsMeasuredFromAHandfulOfSales()
    {
        var watched = new[] { new WatchedSpan(Now.AddHours(-24), Now) };
        Assert.Null(CaptureShareEstimator.Measure(Sales(4, 50, Now.AddHours(-1)), watched, 1, true, 1_950m, Now));
    }

    [Fact]
    public void TheShareIsOurUnitsOverWhatTheMarketSellsInTheTimeWatched()
    {
        var watched = new[] { new WatchedSpan(Now.AddHours(-24), Now) };

        var share = CaptureShareEstimator.Measure(Sales(10, 50, Now.AddHours(-1)), watched, 1, true, 1_950m, Now);

        // 500 units against 1,950 a day for the 23h58m that counted.
        Assert.NotNull(share);
        Assert.InRange(share!.Value, 0.256m, 0.257m);
    }

    [Fact]
    public void SalesDeliveredAsALoginStartsAreNotCounted()
    {
        var watched = new[] { new WatchedSpan(Now.AddHours(-24), Now) };
        var burst = Sales(10, 50, Now.AddHours(-24).AddSeconds(30));

        Assert.Null(CaptureShareEstimator.Measure(burst, watched, 1, true, 1_950m, Now));
    }

    [Fact]
    public void OneLuckyStretchCannotSwingSizingPastTheClamp()
    {
        var watched = new[] { new WatchedSpan(Now.AddHours(-24), Now) };

        var share = CaptureShareEstimator.Measure(Sales(20, 999, Now.AddHours(-1)), watched, 1, true, 100m, Now);

        Assert.Equal(CaptureShareEstimator.MaximumShare, share);
    }

    [Fact]
    public void MarksWithinTheGapExtendOneSpanAndASilenceStartsAnother()
    {
        var memory = new PriceMemory();
        memory.MarkWatching(Now);
        memory.MarkWatching(Now.AddMinutes(4));
        memory.MarkWatching(Now.AddMinutes(8));
        memory.MarkWatching(Now.AddMinutes(30));

        var spans = memory.WatchedSince(Now.AddDays(-1));

        Assert.Equal(2, spans.Count);
        Assert.Equal(TimeSpan.FromMinutes(8), spans[0].Duration);
        Assert.Equal(Now.AddMinutes(30), spans[1].Start);
    }

    [Fact]
    public void WatchedTimeSurvivesAReload()
    {
        var memory = new PriceMemory();
        memory.MarkWatching(Now);
        memory.MarkWatching(Now.AddMinutes(3));

        var reloaded = new PriceMemory(memory.ToData());

        Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(reloaded.WatchedSince(Now.AddDays(-1))).Duration);
    }

    [Fact]
    public void CoverageIsMeasuredOnOurShareOfTheMarket()
    {
        // 1,950 a day at 30% is 585 of ours: seven days is 4,095 units, not 13,650.
        Assert.True(InventoryCoveragePolicy.CanAdd(1_000, 99, 1_950m, 0.3m, 7m, 1m));
        Assert.False(InventoryCoveragePolicy.CanAdd(4_100, 99, 1_950m, 0.3m, 7m, 1m));
        Assert.True(InventoryCoveragePolicy.CanAdd(4_100, 99, 1_950m, 1m, 7m, 1m));
    }

    [Fact]
    public void AThinMarketCanStillBeStartedWithOneLot()
    {
        // 30 a day of ours cannot carry 99 in 2.5 days, but the market's 100 can.
        Assert.True(InventoryCoveragePolicy.CanAdd(0, 99, 100m, 0.3m, 1.5m, 1m));
        Assert.False(InventoryCoveragePolicy.CanAdd(99, 99, 100m, 0.3m, 1.5m, 1m));
        // A market that could not carry one lot at full rate is still refused.
        Assert.False(InventoryCoveragePolicy.CanAdd(0, 99, 10m, 0.3m, 1.5m, 1m));
    }

    [Fact]
    public void TheBulkTargetIsSizedOnTheCaptureShare()
    {
        var market = Market(1, 2_600, 1_950m, Enumerable.Range(0, 60).Select(i => (1_500u, 99u)).ToArray());
        var rule = Rule(1, preferred: true);
        var bulk = ProcurementEconomicPolicy.Default with { PreferredCoverageDays = 7m, BulkPreferredPurchases = true };

        var whole = Plan(market, rule, bulk);
        var share = Plan(market, rule, bulk with { CaptureShare = 0.3m });

        Assert.Equal(60, whole.Orders.Count);
        // Lots are added while holdings are under 4,095 and stay within 4,680.
        Assert.Equal(42, share.Orders.Count);
        Assert.All(share.Orders, x => Assert.Equal(0.3m, x.CaptureShare));
    }

    [Fact]
    public void AMeasuredShareReplacesTheAssumedOne()
    {
        var policy = ProcurementEconomicPolicy.Default with
        {
            CaptureShare = 0.3m,
            MeasuredCaptureShares = new Dictionary<(uint, bool), decimal> { [(1, true)] = 0.55m },
        };

        Assert.Equal(0.55m, policy.CaptureShareFor(1, true));
        Assert.Equal(0.3m, policy.CaptureShareFor(1, false));
        Assert.Equal(0.3m, policy.CaptureShareFor(2, true));
    }

    [Fact]
    public void AConsumableLineGetsTheFoodMarginBarWithoutBeingPreferred()
    {
        // 2,100 against a 2,599 resale is 12% net: over the 10% food bar, under the
        // 14% one for other high-volume stock.
        var market = Market(1, 2_600, 500m, (2_100u, 99u));
        var food = Rule(1, preferred: false);
        food.Consumable = true;

        Assert.Single(Plan(market, food, ProcurementEconomicPolicy.Default).Orders);
        Assert.Empty(Plan(market, Rule(1, preferred: false), ProcurementEconomicPolicy.Default).Orders);
    }

    [Fact]
    public void CoreStockIsHeldToTheProfitFloorPerLot()
    {
        // 895 profit a unit: 99 of them clear 25,000, ten do not.
        var market = Market(1, 2_600, 1_950m, (1_500u, 10u), (1_500u, 99u));

        var plan = Plan(market, Rule(1, preferred: true), ProcurementEconomicPolicy.Default, minimumProfitPerLot: 25_000);

        Assert.Equal(99u, Assert.Single(plan.Orders).Quantity);
        Assert.Contains(plan.DecisionLog, x => !x.Selected && x.Quantity == 10 && x.Reason.Contains("insufficient slot value"));
    }

    [Fact]
    public void ALineListedSmallerThanItIsBoughtIsCappedInListingsNotLots()
    {
        // Nachos: bought in lots of up to 99, listed 20 at a time. 60 in the bags fill
        // three listings; at 30% of 147 a day, 2.5 days of demand is six listings,
        // and 40 more units still fit the coverage ceiling. Counted in lots, the
        // demand was two "slots" against three held, and nothing more was bought.
        var nachos = new ProcurementMarketItem(49239, "Nachos",
            [new(49239, 1, 1, "Siren", 1, 5_594, 20, true), new(49239, 100, 600, "Cactuar", 2, 2_300, 40, true)],
            Enumerable.Range(1, 4).Select(i => new ProcurementSale(5_594, 30, true, DateTimeOffset.UtcNow.AddDays(-i * 0.5))).ToArray(),
            HqSalesPerDay: 147m);
        var rule = new ProcurementRule
        {
            ItemId = 49239, ItemName = "Nachos", TargetStackSize = 99, ListingStackSize = 20, MaximumSaleSlots = 1,
            MinimumWeeklyUnitsSold = 20, AllowHighQuality = true, RequireHighQuality = true, Consumable = true,
        };

        var plan = planner.BuildPlan(new([nachos], [rule], 20_000_000, 100, 140, 20m, 100,
            HomeWorld: "Siren", OwnedStock: [new(49239, true, 60, 3, PortfolioTier.Secondary)],
            Portfolio: new(75m, 10m, 2_500), PortfolioCapacitySlots: 60,
            Economics: ProcurementEconomicPolicy.Default with { CaptureShare = 0.3m }));

        Assert.Equal(40u, Assert.Single(plan.Orders).Quantity);
    }

    [Fact]
    public void TheListingSizeIsTheRulesOwnThenWholeStacksForCuratedFoodThenTheLot()
    {
        Assert.Equal(50, ResaleStockPolicy.ListingSize(new() { ListingStackSize = 50, TargetStackSize = 99 }, "Caramel Popcorn"));
        Assert.Equal(99, ResaleStockPolicy.ListingSize(new() { TargetStackSize = 20 }, "Caramel Popcorn"));
        Assert.Equal(20, ResaleStockPolicy.ListingSize(new() { TargetStackSize = 20 }, "Savage Aim Materia XII"));
        Assert.Equal(99, ResaleStockPolicy.ListingSize(null, "Popoto Potage"));
    }

    [Fact]
    public void AListingSizedToItsBuyersIsRaisedUntilItIsWorthAHundredThousand()
    {
        var materia = new ProcurementRule { ListingStackSize = 4, TargetStackSize = 20 };
        Assert.Equal(11, ResaleStockPolicy.ListingQuantity(materia, "Craftsman's Command Materia XI", 9_698));
        Assert.Equal(4, ResaleStockPolicy.ListingQuantity(materia, "Craftsman's Command Materia XI", 30_000));
        var popcorn = new ProcurementRule { ListingStackSize = 50 };
        Assert.Equal(53, ResaleStockPolicy.ListingQuantity(popcorn, "Caramel Popcorn", 1_900));
    }

    [Fact]
    public void LinesWithoutAnExplicitListingSizeKeepTheSizeTheyAlwaysHad()
    {
        var dye = new ProcurementRule { TargetStackSize = 20, LiquidateOnly = true };
        Assert.Equal(20, ResaleStockPolicy.ListingQuantity(dye, "General-purpose Dark Red Dye", 300));
    }

    private ProcurementPlan Plan(ProcurementMarketItem market, ProcurementRule rule, ProcurementEconomicPolicy policy,
        uint minimumProfitPerLot = 2_500) =>
        planner.BuildPlan(new([market], [rule], 20_000_000, 100, 140, 20m, 100,
            HomeWorld: "Siren", Portfolio: new(75m, 10m, minimumProfitPerLot), PortfolioCapacitySlots: 60,
            Economics: policy));

    private static ProcurementRule Rule(uint id, bool preferred) => new()
    {
        ItemId = id, ItemName = "Caramel Popcorn", TargetStackSize = 99, MaximumSaleSlots = 8,
        MinimumWeeklyUnitsSold = 20, PreferredStock = preferred, AllowHighQuality = true, RequireHighQuality = true,
    };

    private static ProcurementMarketItem Market(uint id, uint homePrice, decimal unitsPerDay,
        params (uint Price, uint Quantity)[] offers) =>
        new(id, "Caramel Popcorn",
            [new(id, 1, 1, "Siren", 1, homePrice, 99, true),
             .. offers.Select((o, i) => new ProcurementMarketListing(id, (ulong)(100 + i), (ulong)(600 + i),
                 "Cactuar", 2, o.Price, o.Quantity, true))],
            Enumerable.Range(1, 4).Select(i => new ProcurementSale(homePrice, 175, true, DateTimeOffset.UtcNow.AddDays(-i * 0.5)))
                .ToArray(),
            HqSalesPerDay: unitsPerDay);

    private static RetainerSale[] Sales(int count, uint quantity, DateTimeOffset at) =>
        Enumerable.Range(0, count).Select(i => new RetainerSale(at.AddSeconds(i), 1, true, quantity, quantity * 2_400u, 1))
            .ToArray();
}
