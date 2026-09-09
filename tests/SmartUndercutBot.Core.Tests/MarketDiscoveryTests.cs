using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Core.Tests.Automation;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// Market intelligence and purchase authorization are separate concerns. A remote
/// source may recommend what to look at; only a fresh live board reading, with
/// every existing guard, can authorize a purchase.
/// </summary>
public sealed class MarketDiscoveryTests
{
    private sealed class FakeStatistics(
        IReadOnlyList<MarketStatistic> results, bool fails = false) : IMarketStatisticsProvider
    {
        public int Calls { get; private set; }
        public string Name => "Fake statistics";
        public bool IsEnabled { get; set; } = true;

        public Task<IReadOnlyList<MarketStatistic>> GetStatisticsAsync(
            IReadOnlyList<uint> itemIds, string scope, CancellationToken cancellationToken)
        {
            Calls++;
            return fails
                ? Task.FromException<IReadOnlyList<MarketStatistic>>(new HttpRequestException("discovery offline"))
                : Task.FromResult(results);
        }
    }

    private static MarketStatistic Stat(uint id, string name, uint hq = 3_000, decimal hqPerDay = 400m) =>
        new(id, name, 1_000, hq, 300m, hqPerDay, DateTimeOffset.UtcNow);

    private static MarketItemFacts Food(uint id, string name) => new(id, name, true, true, true, 99);

    [Fact]
    public void ExternalDiscoveryIsOptInAndIsDisarmedByTheUpgrade()
    {
        // Nothing external is contacted unless the setting is switched on.
        var fresh = new Configuration();
        fresh.Normalize();
        Assert.False(fresh.MarketDiscoveryEnabled);

        // A 1.0.0.58 configuration that had it on is disarmed on upgrade, and the
        // rules it suggested are retired rather than left behind.
        var upgraded = new Configuration { Version = 36, MarketDiscoveryEnabled = true };
        upgraded.ProcurementRules.Add(new() { ItemId = 42, ItemName = "Discovered Stew", DiscoveredAutomatically = true });
        upgraded.ProcurementRules.Add(new() { ItemId = 1, ItemName = "Caramel Popcorn", PreferredStock = true });
        upgraded.Normalize();
        Assert.False(upgraded.MarketDiscoveryEnabled);
        Assert.DoesNotContain(upgraded.ProcurementRules, x => x.DiscoveredAutomatically);
        // The curated portfolio is untouched by the rollback.
        Assert.Contains(upgraded.ProcurementRules, x => x.ItemId == 1 && x.PreferredStock);
    }

    [Fact]
    public void RankingHappensBeforeAnyGameDataIsRead()
    {
        // A whole-region dataset is ~17,000 items. Resolving every one of them in
        // the game's own data, off the framework thread, for a dozen results is not
        // acceptable: the numeric shortlist has to come first.
        var looked = new List<uint>();
        var statistics = Enumerable.Range(1, 5_000)
            .Select(i => new MarketStatistic((uint)i, $"Item {i}", 10, (uint)i, 5m, 100m, DateTimeOffset.UtcNow))
            .ToArray();

        var proposed = MarketDiscoveryPolicy.Propose(statistics, id =>
        {
            looked.Add(id);
            return Food(id, $"Item {id}");
        }, new HashSet<uint>(), maximumRules: 12, maximumLookups: 500);

        Assert.Equal(500, looked.Count);
        Assert.Equal(12, proposed.Count);
        // The shortlist keeps the most valuable items, not an arbitrary 500.
        Assert.Contains(5_000u, looked);
        Assert.DoesNotContain(1u, looked);
    }

    [Fact]
    public void DiscoveryProposesHighValueFoodAndRejectsEverythingElse()
    {
        var facts = new Dictionary<uint, MarketItemFacts>
        {
            [1] = Food(1, "Rich Stew"),
            [2] = new(2, "Untradable Stew", false, true, true, 99),
            [3] = new(3, "Iron Ingot", true, true, IsFoodOrMedicine: false, 999),
            [4] = Food(4, "Cheap Snack"),
            [5] = Food(5, "Slow Delicacy"),
        };
        var statistics = new[]
        {
            Stat(1, "Rich Stew"),
            Stat(2, "Untradable Stew"),
            Stat(3, "Iron Ingot"),
            Stat(4, "Cheap Snack", hq: 100),          // 9,900 gil a stack: not worth a slot
            Stat(5, "Slow Delicacy", hqPerDay: 3m),   // valuable but nothing buys it
            Stat(6, "Item That Does Not Exist"),      // not in the local item sheet
        };

        var proposed = MarketDiscoveryPolicy.Propose(statistics, id => facts.GetValueOrDefault(id), new HashSet<uint>());

        var rule = Assert.Single(proposed);
        Assert.Equal(1u, rule.ItemId);
        Assert.Equal("Rich Stew", rule.ItemName);
        Assert.True(rule.PreferredStock);
        Assert.True(rule.DiscoveredAutomatically);
        Assert.True(rule.RequireHighQuality);
        Assert.Equal(99, rule.TargetStackSize);
    }

    [Fact]
    public void DiscoveryNeverTouchesRulesTheUserAlreadyConfigured()
    {
        var proposed = MarketDiscoveryPolicy.Propose(
            [Stat(1, "Rich Stew")], _ => Food(1, "Rich Stew"), new HashSet<uint> { 1 });
        Assert.Empty(proposed);
    }

    [Fact]
    public void SaddlebagRawStatisticsAreParsedIntoItemStatistics()
    {
        // The documented shape, as returned by POST /api/ffxivrawstats.
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "4698": {"itemID":4698,"mainCategory":5,"subCategory":45,"medianNQ":65,"averageNQ":282,
                   "salesAmountNQ":19,"quantitySoldNQ":430,"medianHQ":299,"averageHQ":266,
                   "salesAmountHQ":12,"quantitySoldHQ":136,"lastUpdateTimeUnix":1788856100,
                   "itemName":"Honey Muffin"}
        }
        """);
        var statistic = Assert.Single(SaddlebagStatisticsProvider.Parse(document.RootElement));
        Assert.Equal(4698u, statistic.ItemId);
        Assert.Equal("Honey Muffin", statistic.ItemName);
        Assert.Equal(65u, statistic.MedianNqPrice);
        Assert.Equal(299u, statistic.MedianHqPrice);
        Assert.Equal(430m, statistic.NqUnitsSoldPerDay);
        Assert.Equal(136m, statistic.HqUnitsSoldPerDay);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788856100), statistic.UpdatedAt);
    }

    [Fact]
    public void UniversalisAggregatedResponseYieldsHintsAndStatisticsOnly()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {"results":[{"itemId":44096,
          "nq":{"minListing":{"dc":{"price":400,"worldId":53},"region":{"price":390,"worldId":91}},
                "averageSalePrice":{"region":{"price":1136.18}},
                "dailySaleVelocity":{"dc":{"quantity":88.03}}},
          "hq":{"minListing":{"world":{"price":750,"worldId":53}},
                "averageSalePrice":{"region":{"price":2375.98}},
                "dailySaleVelocity":{"region":{"quantity":140.14}}}}],
         "failedItems":[]}
        """);
        var worlds = new Dictionary<uint, string> { [53] = "Adamantoise", [91] = "Cactuar" };

        var hints = UniversalisAggregatedParser.ParseHints(document.RootElement, id => worlds.GetValueOrDefault(id));
        Assert.Equal(3, hints.Count);
        Assert.Contains(hints, x => x is { WorldName: "Adamantoise", PricePerUnit: 400, IsHighQuality: false });
        Assert.Contains(hints, x => x is { WorldName: "Cactuar", PricePerUnit: 390, IsHighQuality: false });
        Assert.Contains(hints, x => x is { WorldName: "Adamantoise", PricePerUnit: 750, IsHighQuality: true });
        // The narrowest scope that answered supplies the rate.
        Assert.Equal(88.03m, hints.First(x => !x.IsHighQuality).SalesPerDay);

        var statistic = Assert.Single(UniversalisAggregatedParser.ParseStatistics(
            document.RootElement, DateTimeOffset.UnixEpoch));
        Assert.Equal(1136u, statistic.MedianNqPrice);
        Assert.Equal(2375u, statistic.MedianHqPrice);
        Assert.Equal(140.14m, statistic.HqUnitsSoldPerDay);
        // Hints are a distinct type from listings, so cached data cannot be
        // mistaken for something the planner may buy.
        Assert.IsType<MarketPriceHint>(hints[0]);
    }

    [Fact]
    public void UnknownWorldIdsAndMalformedAggregatesAreIgnored()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {"results":[{"itemId":1,"nq":{"minListing":{"region":{"price":10,"worldId":999}}}},
                    {"itemId":0,"nq":{"minListing":{"region":{"price":10,"worldId":53}}}},
                    {"nq":"not an object"}]}
        """);
        Assert.Empty(UniversalisAggregatedParser.ParseHints(document.RootElement, _ => null));
        Assert.Empty(SaddlebagStatisticsProvider.Parse(document.RootElement.GetProperty("results")));
    }

    [Fact]
    public void FailedDiscoveryDoesNotStopNormalCuratedShopping()
    {
        var statistics = new FakeStatistics([], fails: true);
        using var run = new ProcurementControllerTests.Route(priority: true, statistics);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.MarketDiscoveryEnabled = true;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;

        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);

        // The curated flip is still bought, and the failure is reported plainly.
        Assert.Equal(("Cactuar", 1u, 99u), Assert.Single(run.Game.Bought));
        Assert.True(statistics.Calls > 0, "discovery should have been attempted");
        Assert.True(Settles(() => run.Discovery!.Status.Contains("unavailable")));
        Assert.Contains(run.Log.Messages, m => m.Contains("MARKET DISCOVERY") && m.Contains("unavailable"));
        Assert.DoesNotContain(run.Config.Current.ProcurementRules, r => r.DiscoveredAutomatically);
    }

    [Fact]
    public void CachedStatisticsCannotAuthorizeAPurchaseWithoutFreshLiveValidation()
    {
        // Statistics say item 42 is superb, and discovery duly adds the rule. The
        // live board never offers it, so nothing is ever bought.
        var statistics = new FakeStatistics([Stat(42, "Discovered Stew", hq: 9_000, hqPerDay: 900m)]);
        using var run = new ProcurementControllerTests.Route(priority: true, statistics);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.MarketDiscoveryEnabled = true;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.DemandMarkets = [
            new(1, "Popcorn", [], [new(2_000, 99, true, DateTimeOffset.UtcNow)]),
            new(42, "Discovered Stew", [], [new(9_000, 900, true, DateTimeOffset.UtcNow)]),
        ];
        // Only the curated flip exists on any live board.
        run.Game.LiveProvider = (world, item) => item != 1 ? [] : world switch
        {
            "Siren" => [new(0, item, 11, 21, 2_000, 99, true, 0)],
            _ => [new(0, item, 10, 20, 1_000, 99, true, 4_950)],
        };

        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.True(Settles(() => run.Discovery!.LastSuccessAt is not null));
        // The second pass runs with the discovered rule already merged in.
        run.Repricing.IsActive = false;
        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);

        Assert.Contains(run.Config.Current.ProcurementRules, r => r.ItemId == 42 && r.DiscoveredAutomatically);
        Assert.DoesNotContain(run.Game.Bought, x => x.Item == 42);
        Assert.Contains(run.Game.Bought, x => x.Item == 1);
    }

    [Fact]
    public void DiscoveryResultsAreCachedAndRefreshedOnTheConfiguredSchedule()
    {
        var statistics = new FakeStatistics([Stat(42, "Discovered Stew")]);
        using var run = new ProcurementControllerTests.Route(priority: true, statistics);
        run.Config.Current.MarketDiscoveryEnabled = true;
        run.Config.Current.MarketDiscoveryCacheHours = 24;
        var discovery = run.Discovery!;

        discovery.RefreshIfDue();
        Assert.True(Settles(() => discovery.LastSuccessAt is not null));
        var firstSuccess = discovery.LastSuccessAt;
        Assert.Equal(1, statistics.Calls);

        // Inside the cache lifetime nothing is fetched again.
        discovery.RefreshIfDue();
        run.Tick(23 * 3600);
        discovery.RefreshIfDue();
        Assert.Equal(1, statistics.Calls);

        run.Tick(2 * 3600);
        discovery.RefreshIfDue();
        Assert.True(Settles(() => discovery.LastSuccessAt != firstSuccess));
        Assert.Equal(2, statistics.Calls);
        Assert.Equal(1, discovery.ApplyPendingDiscoveries());
        Assert.Contains(run.Config.Current.ProcurementRules, r => r.ItemId == 42 && r.PreferredStock);
    }

    [Fact]
    public void DisabledDiscoveryIsNeverCalled()
    {
        var statistics = new FakeStatistics([Stat(42, "Discovered Stew")]);
        using var run = new ProcurementControllerTests.Route(priority: true, statistics);
        run.Config.Current.MarketDiscoveryEnabled = false;

        run.Controller.ScanNow();
        for (var i = 0; i < 50; i++) run.Tick();
        run.Discovery!.RefreshIfDue();
        Assert.Equal(0, statistics.Calls);
        Assert.DoesNotContain(run.Config.Current.ProcurementRules, r => r.DiscoveredAutomatically);
    }

    // Discovery runs off the framework thread on purpose, so it can never stall
    // the game loop. Wait for it rather than assuming it has finished.
    private static bool Settles(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));
}
