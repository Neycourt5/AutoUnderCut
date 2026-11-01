using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// The v46 migration that applies the answers to the 2026-11-01 review, the
/// clearing and restoring of old automatic minimums, and food and medicine
/// discovered from the home world's own sales ranking.
/// </summary>
public sealed class ReviewDecisionMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // Shaped like the live settings file, which is still at version 44.
    private static Configuration LiveShaped() => new()
    {
        Version = 44,
        RepeatMinimumMinutes = 5,
        RepeatMaximumMinutes = 5,
        ProcurementFastMoverRoiPercent = 10m,
        ProcurementHighVolumeRoiPercent = 14m,
        ProcurementMinimumRoiPercent = 10.5m,
        ProcurementAbsoluteMinimumRoiPercent = 8m,
        ProcurementFillRoiPercent = 10m,
        ProcurementMinimumProfitPerSaleSlot = 2_500,
        ProcurementEmergencyMaximumSlotsPerItem = 20,
        ProcurementCaptureSharePercent = 100m,
        SecondaryLinesSeeded = true,
        ProcurementRules =
        [
            new() { ItemId = 49240, ItemName = "Caramel Popcorn", PreferredStock = true, AllowHighQuality = true, RequireHighQuality = true },
            new() { ItemId = 49234, ItemName = "Grade 4 Gemdraught of Strength", PreferredStock = true, AllowHighQuality = true, RequireHighQuality = true },
            new() { ItemId = 41767, ItemName = "Craftsman's Command Materia XI", TargetStackSize = 20 },
            new() { ItemId = 41780, ItemName = "Craftsman's Command Materia XII", TargetStackSize = 20 },
            new() { ItemId = 13710, ItemName = "General-purpose Dark Brown Dye", TargetStackSize = 20 },
        ],
        PerItemRules = new()
        {
            // Written automatically by older versions: a margin ratchet and a
            // minimum on a basis no longer tracked.
            [13710] = new() { MinimumPrice = 1_194, MinimumMarginPercent = 20m, CostBasis = 945 },
            // A tracked position whose old minimum returns the moment it sells out.
            [49234] = new() { MinimumPrice = 5_930, MinimumMarginPercent = 20m, CostBasis = 3_882, CostBasisUnits = 764, AcquisitionFloor = 4_088 },
            [49240] = new() { CostBasis = 2_142, CostBasisUnits = 2_929, AcquisitionFloor = 2_256 },
            // Explicitly opted in, so never touched.
            [13708] = new() { MinimumPrice = 39_000, ApplyMinimumPriceToPurchasedStock = true },
        },
    };

    [Fact]
    public void TheAnswersToTheReviewAreAppliedOnUpgrade()
    {
        var config = LiveShaped();
        config.Normalize();

        Assert.Equal(46, config.Version);
        Assert.Equal(20m, config.ProcurementFastMoverRoiPercent);
        Assert.Equal(25m, config.ProcurementHighVolumeRoiPercent);
        Assert.Equal(25m, config.ProcurementMinimumRoiPercent);
        Assert.Equal(20m, config.ProcurementAbsoluteMinimumRoiPercent);
        // Equal to the standard bar, so the thinner-margin top-up never runs.
        Assert.Equal(config.ProcurementMinimumRoiPercent, config.ProcurementFillRoiPercent);
        Assert.Equal(25_000u, config.ProcurementMinimumProfitPerSaleSlot);
        Assert.Equal(9, config.ProcurementEmergencyMaximumSlotsPerItem);
        Assert.Equal(30m, config.ProcurementCaptureSharePercent);
        Assert.Equal(2, config.RepeatMinimumMinutes);
        Assert.Equal(2, config.RepeatMaximumMinutes);
        Assert.False(config.SecondaryLinesSeeded);
        Assert.Equal(0.3m, config.EconomicPolicy.CaptureShare);
    }

    [Fact]
    public void ListingSizesAndTheFoodBarFollowTheDecidedLines()
    {
        var config = LiveShaped();
        config.Normalize();

        var rules = config.ProcurementRules.ToDictionary(x => x.ItemId);
        Assert.Equal((50, true), (rules[49240].ListingStackSize, rules[49240].Consumable));
        Assert.Equal((99, true), (rules[49234].ListingStackSize, rules[49234].Consumable));
        Assert.Equal((4, false), (rules[41767].ListingStackSize, rules[41767].Consumable));
        Assert.Equal((3, false), (rules[41780].ListingStackSize, rules[41780].Consumable));
        Assert.Equal(0, rules[13710].ListingStackSize);
    }

    [Fact]
    public void OldAutomaticMinimumsAreClearedAndAKeptCopyRecordsThem()
    {
        var config = LiveShaped();
        var dyeFloorBefore = PositionCostPolicy.MinimumListingPrice(config.PerItemRules[13710].Clone());
        config.Normalize();

        var dye = config.PerItemRules[13710];
        Assert.Equal((1u, 0m, 0u), (dye.MinimumPrice, dye.MinimumMarginPercent, dye.CostBasis));
        Assert.Equal(1_194u, dyeFloorBefore);
        Assert.Equal(1u, PositionCostPolicy.MinimumListingPrice(dye));
        // The tracked position keeps its live basis; only the old minimum goes.
        var strength = config.PerItemRules[49234];
        Assert.Equal((1u, 0m, 3_882u, 764u, 4_088u),
            (strength.MinimumPrice, strength.MinimumMarginPercent, strength.CostBasis, strength.CostBasisUnits, strength.AcquisitionFloor));
        Assert.Equal(39_000u, config.PerItemRules[13708].MinimumPrice);
        Assert.Equal(2_142u, config.PerItemRules[49240].CostBasis);

        Assert.Equal(new uint[] { 13710, 49234 }, config.ClearedLegacyMinimums.Keys.Order().ToArray());
        var kept = config.ClearedLegacyMinimums[13710];
        Assert.Equal((1_194u, 20m, 945u), (kept.MinimumPrice, kept.MinimumMarginPercent, kept.CostBasis));
        Assert.Equal(0u, config.ClearedLegacyMinimums[49234].CostBasis);
        Assert.False(config.ClearedLegacyMinimumsReported);
    }

    [Fact]
    public void ClearedMinimumsCanBePutBack()
    {
        var config = LiveShaped();
        config.Normalize();

        Assert.Equal(2, config.RestoreLegacyMinimums());

        var dye = config.PerItemRules[13710];
        Assert.Equal((1_194u, 20m, 945u), (dye.MinimumPrice, dye.MinimumMarginPercent, dye.CostBasis));
        var strength = config.PerItemRules[49234];
        Assert.Equal((5_930u, 3_882u), (strength.MinimumPrice, strength.CostBasis));
        Assert.Empty(config.ClearedLegacyMinimums);
    }

    [Fact]
    public void TheMigrationRunsOnceSoAMinimumSetAfterwardsStays()
    {
        var config = LiveShaped();
        config.Normalize();
        config.PerItemRules[13710].MinimumPrice = 777;

        config.Normalize();

        Assert.Equal(777u, config.PerItemRules[13710].MinimumPrice);
        Assert.Equal(1_194u, config.ClearedLegacyMinimums[13710].MinimumPrice);
    }

    [Fact]
    public void TwoMinutePassesAreNotRaisedBackToFive()
    {
        var config = new Configuration { RepeatMinimumMinutes = 2, RepeatMaximumMinutes = 2 };
        config.Normalize();
        Assert.Equal((2, 2), (config.RepeatMinimumMinutes, config.RepeatMaximumMinutes));
    }

    [Fact]
    public void OnlyFoodAndMedicineFromTheHomeRankingAreProposedAsLines()
    {
        var proposals = MarketDiscoveryPolicy.ProposeFromHomeRanking(
        [
            Candidate(49239, "Nachos", hq: true, units: 147m, sales: 7.6m, home: 5_594, cheapest: 2_300),
            // Materia sells and has a spread, but is never added automatically.
            Candidate(41767, "Craftsman's Command Materia XI", hq: false, units: 159m, sales: 17.8m, home: 9_698, cheapest: 4_000),
            // Food with no spread left.
            Candidate(49244, "Popoto Potage", hq: true, units: 518m, sales: 9m, home: 2_996, cheapest: 2_800),
            // The NQ ranking row of an item bought only in HQ.
            Candidate(49239, "Nachos", hq: false, units: 40m, sales: 6m, home: 3_000, cheapest: 1_000),
            // Food the player already trades.
            Candidate(49240, "Caramel Popcorn", hq: true, units: 1_950m, sales: 33m, home: 2_894, cheapest: 1_500),
        ], Lookup, new HashSet<uint> { 49240 }, FeeModel.Default);

        var line = Assert.Single(proposals);
        Assert.Equal(49239u, line.ItemId);
        Assert.True(line.Consumable);
        Assert.True(line.DiscoveredAutomatically);
        Assert.False(line.PreferredStock);
        Assert.True(line.RequireHighQuality);
        // 147 units over 7.6 sales a day: buyers take about 20 at a time.
        Assert.Equal(20, line.ListingStackSize);
        Assert.Equal(0u, line.BagReserveQuantity);
    }

    [Theory]
    [InlineData(0.8, 1)]
    [InlineData(4.4, 4)]
    [InlineData(19.3, 20)]
    [InlineData(50.5, 50)]
    [InlineData(97, 99)]
    public void BuyerListingSizesAreRounded(double unitsPerSale, int expected) =>
        Assert.Equal(expected, MarketDiscoveryPolicy.BuyerListingSize((decimal)unitsPerSale));

    [Fact]
    public void AtMostTwoLinesAreAddedPerScanAndSixInAll()
    {
        var (discovery, config) = Discovery();
        discovery.Offer([Line(1), Line(2), Line(3)]);
        Assert.Equal(2, discovery.ApplyPendingDiscoveries());
        discovery.Offer([Line(1), Line(2), Line(3), Line(4), Line(5), Line(6), Line(7), Line(8)]);
        Assert.Equal(2, discovery.ApplyPendingDiscoveries());
        discovery.Offer([.. Enumerable.Range(1, 8).Select(i => Line((uint)i))]);
        discovery.ApplyPendingDiscoveries();
        discovery.Offer([.. Enumerable.Range(1, 8).Select(i => Line((uint)i))]);
        discovery.ApplyPendingDiscoveries();

        Assert.Equal(MarketDiscoveryService.MaximumAutomaticLines,
            config.Current.ProcurementRules.Count(x => x.DiscoveredAutomatically && !x.LiquidateOnly));
    }

    [Fact]
    public void ALineIsRetiredToSellOffOnlyAfterThreeScansWithoutQualifying()
    {
        var (discovery, config) = Discovery();
        discovery.Offer([Line(1)]);
        discovery.ApplyPendingDiscoveries();
        var rule = config.Current.ProcurementRules.Single(x => x.ItemId == 1);

        for (var scan = 1; scan <= 2; scan++)
        {
            discovery.Offer([]);
            discovery.ApplyPendingDiscoveries();
            Assert.False(rule.LiquidateOnly);
        }
        discovery.Offer([]);
        discovery.ApplyPendingDiscoveries();

        // Kept as sell-off, not removed: stock already bought still lists.
        Assert.True(rule.LiquidateOnly);
        Assert.Contains(config.Current.ProcurementRules, x => x.ItemId == 1);
    }

    [Fact]
    public void AQualifyingScanResetsTheCountAndRevivesARetiredLine()
    {
        var (discovery, config) = Discovery();
        discovery.Offer([Line(1)]);
        discovery.ApplyPendingDiscoveries();
        var rule = config.Current.ProcurementRules.Single(x => x.ItemId == 1);
        for (var scan = 0; scan < 3; scan++)
        {
            discovery.Offer([]);
            discovery.ApplyPendingDiscoveries();
        }
        Assert.True(rule.LiquidateOnly);

        discovery.Offer([Line(1)]);
        discovery.ApplyPendingDiscoveries();

        Assert.False(rule.LiquidateOnly);
        Assert.Equal(0, rule.DiscoveryMisses);
    }

    [Fact]
    public void DiscoveredLinesAreNeverPinnedAsCoreStock()
    {
        var (discovery, config) = Discovery();
        // Enough agreeing scans and a realised spread would once have pinned it.
        config.Current.PerItemRules[1] = new PricingRule { CostBasis = 1_000 };
        for (var scan = 0; scan < 10; scan++)
        {
            discovery.Offer([Line(1, price: 400_000, unitsPerDay: 500m)]);
            discovery.ApplyPendingDiscoveries();
        }

        Assert.False(config.Current.ProcurementRules.Single(x => x.ItemId == 1).PreferredStock);
    }

    private static MarketItemFacts? Lookup(uint id) => id switch
    {
        49239 => new(id, "Nachos", true, true, true, 999, "Meals"),
        49244 => new(id, "Popoto Potage", true, true, true, 999, "Meals"),
        49240 => new(id, "Caramel Popcorn", true, true, true, 999, "Meals"),
        41767 => new(id, "Craftsman's Command Materia XI", true, false, false, 999, "Materia"),
        _ => null,
    };

    private static MarketCandidate Candidate(uint id, string name, bool hq, decimal units, decimal sales, uint home, uint cheapest) =>
        new(id, name, hq, "Meals", units, sales, home, cheapest, "Cactuar", "", PriceSource.Saddlebag, Now);

    private static ProcurementRule Line(uint id, uint price = 5_000, decimal unitsPerDay = 150m) => new()
    {
        ItemId = id, ItemName = $"Food {id}", TargetStackSize = 99, ListingStackSize = 20, DiscoveredAutomatically = true,
        Consumable = true, LastObservedUnitPrice = price, LastObservedSalesPerDay = unitsPerDay,
    };

    private static (MarketDiscoveryService Discovery, ConfigurationService Config) Discovery()
    {
        var config = new ConfigurationService();
        return (new MarketDiscoveryService(new NoStatistics(), _ => null, config, new AutomationLog()), config);
    }

    private sealed class NoStatistics : IMarketStatisticsProvider
    {
        public string Name => "None";
        public bool IsEnabled => false;
        public Task<IReadOnlyList<MarketStatistic>> GetStatisticsAsync(
            IReadOnlyList<uint> itemIds, string scope, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MarketStatistic>>([]);
    }
}
