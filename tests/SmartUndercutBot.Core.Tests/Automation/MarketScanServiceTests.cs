using System.Text.Json;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests.Automation;

public sealed class MarketScanServiceTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1793508000);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeSource : IMarketScanSource
    {
        public bool Answer { get; set; } = true;
        public List<string> Calls { get; } = [];

        public Task<JsonDocument?> RegionListingsAsync(IReadOnlyList<uint> itemIds, string region, bool highQuality,
            int listings, CancellationToken token)
        {
            Calls.Add($"region {region} {string.Join(',', itemIds)} hq={highQuality}");
            return Reply("""
            {"items":{"49240":{"itemID":49240,"worldUploadTimes":{"64":1793507000000},"listings":[
              {"lastReviewTime":1793506900,"pricePerUnit":1500,"quantity":99,"worldName":"Leviathan","worldID":64,"hq":true}]}}}
            """);
        }

        public Task<JsonDocument?> WorldMarketAsync(IReadOnlyList<uint> itemIds, string world, bool highQuality,
            CancellationToken token)
        {
            Calls.Add($"world {world} {string.Join(',', itemIds)} hq={highQuality}");
            return Reply("""
            {"items":{"49240":{"itemID":49240,"lastUploadTime":1793507900000,"hqSaleVelocity":1600,
              "listings":[{"pricePerUnit":2895,"quantity":99,"hq":true,"retainerName":"Other"},
                          {"pricePerUnit":2500,"quantity":99,"hq":true,"retainerName":"Mine"}],
              "recentHistory":[{"hq":true,"pricePerUnit":2900,"quantity":50,"timestamp":1793505970}]}}}
            """);
        }

        public Task<JsonDocument?> AggregatedAsync(IReadOnlyList<uint> itemIds, string scope, CancellationToken token)
        {
            Calls.Add($"aggregated {scope} {string.Join(',', itemIds)}");
            return Reply("""
            {"results":[{"itemId":49240,"hq":{"minListing":{"world":{"price":2895},"dc":{"price":1899,"worldId":40},
              "region":{"price":1450,"worldId":404}}}}]}
            """);
        }

        public Task<JsonDocument?> MarketshareAsync(string world, int hours, int minimumSales, int minimumAveragePrice,
            IReadOnlyList<int> filters, string sortBy, CancellationToken token)
        {
            Calls.Add($"marketshare {world} {hours}h");
            return Reply("""
            {"data":[{"avg":2672,"itemID":"49240","marketValue":39103799,"median":2694,"minPrice":2895,"name":"Caramel Popcorn","purchaseAmount":248,"quantitySold":14567,"state":"stable"},
                     {"avg":10,"itemID":"777","marketValue":5000,"median":10,"minPrice":10,"name":"Untradable","purchaseAmount":40,"quantitySold":500,"state":"stable"}]}
            """);
        }

        private Task<JsonDocument?> Reply(string json) =>
            Task.FromResult(Answer ? JsonDocument.Parse(json) : null);
    }

    private static (MarketScanService Scan, PriceMemory Memory, FakeSource Source, Clock Clock, AutomationLog Log)
        Create(string? home = "Siren")
    {
        var config = new ConfigurationService();
        config.Current.ProcurementRules.Add(new()
        {
            ItemId = 49240, ItemName = "Caramel Popcorn", Enabled = true,
            AllowHighQuality = true, RequireHighQuality = true, PreferredStock = true,
        });
        var memory = new PriceMemory();
        var source = new FakeSource();
        var clock = new Clock();
        var log = new AutomationLog();
        var worlds = new Dictionary<uint, string> { [40] = "Jenova", [404] = "Marilith", [57] = "Siren" };
        var scan = new MarketScanService(source, config, () => memory, () => home,
            () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mine" }, () => new HashSet<ulong>(),
            id => id == 49240 ? new MarketItemFacts(id, "Caramel Popcorn", true, true, true, 999, "Meals") : null,
            id => worlds.GetValueOrDefault(id), log, clock);
        return (scan, memory, source, clock, log);
    }

    private static void WaitFor(MarketScanService scan)
    {
        for (var i = 0; i < 500 && scan.IsScanning; i++)
            Thread.Sleep(10);
        Assert.False(scan.IsScanning);
    }

    [Fact]
    public void ADueScanFillsTheMemoryWithCheapWorldsHomeMarketAndCandidates()
    {
        var (scan, memory, source, clock, log) = Create();
        scan.Update();
        WaitFor(scan);

        var now = clock.GetUtcNow();
        var leviathan = memory.Latest("Leviathan", 49240, true, now, TimeSpan.FromHours(1))!;
        Assert.Equal(1_500u, leviathan.Lowest);
        Assert.Equal(PriceSource.Universalis, leviathan.Source);
        // Our own retainer's cheaper row is not the home floor.
        var home = memory.LatestHome(49240, true, now, TimeSpan.FromHours(1))!;
        Assert.Equal(2_895u, home.LowestCompetitor);
        Assert.Equal(1_600m, home.UnitsPerDay);

        var candidate = Assert.Single(memory.Candidates);
        Assert.Equal("Meals", candidate.Category);
        Assert.Equal(1_450u, candidate.CheapestElsewhere);
        Assert.Equal("Marilith", candidate.CheapestWorld);
        Assert.Contains(log.Messages, x => x.StartsWith("MARKET SCAN:"));
        Assert.Contains(source.Calls, x => x.StartsWith("aggregated Siren"));
    }

    [Fact]
    public void NothingRunsBeforeTheIntervalOrWithoutAHomeWorld()
    {
        var (scan, _, source, clock, _) = Create();
        scan.Update();
        WaitFor(scan);
        var calls = source.Calls.Count;
        clock.Advance(TimeSpan.FromMinutes(10));
        scan.Update();
        WaitFor(scan);
        Assert.Equal(calls, source.Calls.Count);
        clock.Advance(TimeSpan.FromMinutes(25));
        scan.Update();
        WaitFor(scan);
        // The price scan is due again; the daily ranking is not.
        Assert.True(source.Calls.Count > calls);
        Assert.Single(source.Calls, x => x.StartsWith("marketshare"));

        var (offline, _, offlineSource, _, _) = Create(home: null);
        offline.Update();
        Assert.False(offline.IsScanning);
        Assert.Empty(offlineSource.Calls);
    }

    [Fact]
    public void ASourceThatDoesNotAnswerLeavesTheMemoryAloneAndNeverThrows()
    {
        var (scan, memory, source, _, log) = Create();
        source.Answer = false;
        scan.Update();
        WaitFor(scan);
        Assert.Equal(0, memory.WorldSnapshotCount);
        Assert.Empty(memory.Candidates);
        Assert.Contains(log.Messages, x => x.Contains("unanswered") || x.Contains("did not answer"));
    }

    [Fact]
    public void ScanningCanBeSwitchedOffButForcedFromTheDashboard()
    {
        var (scan, _, source, _, _) = Create();
        var config = typeof(MarketScanService)
            .GetField("configuration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(scan) as ConfigurationService;
        config!.Current.MarketScanEnabled = false;
        scan.Update();
        Assert.Empty(source.Calls);
        scan.Update(force: true);
        WaitFor(scan);
        Assert.NotEmpty(source.Calls);
    }
}

public sealed class PriceMemoryServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sub-memory-" + Guid.NewGuid().ToString("N"));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void MemorySurvivesAReloadAndARecoveredBackup()
    {
        var clock = new Clock();
        var log = new AutomationLog();
        var service = new PriceMemoryService(directory, () => 42, log, clock);
        service.Update();
        service.Memory.RecordSale(new(clock.Now, 49240, true, 99, 270_000, 1, "Caramel Popcorn"));
        service.Update();
        service.Flush();
        var path = Path.Combine(directory, "price-memory-42.json");
        Assert.True(File.Exists(path));

        // A second save rotates the first into the backup.
        clock.Now = clock.Now.AddMinutes(3);
        service.Memory.RecordSale(new(clock.Now, 49234, true, 99, 390_000, 1, "Grade 4 Gemdraught of Strength"));
        service.Dispose();
        Assert.True(File.Exists(path + ".bak"));

        var reloaded = new PriceMemoryService(directory, () => 42, log, clock);
        reloaded.Update();
        Assert.Equal(2, reloaded.Memory.SaleCount);

        File.WriteAllText(path, "{ not json");
        var recovered = new PriceMemoryService(directory, () => 42, log, clock);
        recovered.Update();
        Assert.Equal(1, recovered.Memory.SaleCount);
        Assert.Contains(log.Messages, x => x.Contains("could not read price-memory-42.json"));
        // Recovering from the backup repairs the damaged primary on the next save.
        recovered.Dispose();
        Assert.Equal(1, new PriceMemory(System.Text.Json.JsonSerializer.Deserialize<PriceMemoryData>(
            File.ReadAllText(path))).SaleCount);
    }

    [Fact]
    public void NothingIsWrittenWithoutACharacter()
    {
        var service = new PriceMemoryService(directory, () => 0, new AutomationLog(), new Clock());
        service.Update();
        service.Memory.RecordSale(new(DateTimeOffset.UtcNow, 1, true, 1, 1, 0));
        service.Dispose();
        Assert.False(Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any());
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

public sealed class ReviewFixTests
{
    [Fact]
    public void AMissingSaleHistoryNoLongerLetsUnknownCostStockFollowATrollListing()
    {
        var pricing = new PricingStrategyService();
        var rule = new PricingRule { PriceWarDropPercent = 60m, PriceWarAction = PriceWarAction.LeaveUnchanged };
        // No tracked cost, and the history packet did not arrive.
        var listing = new RetainerListing(1, "Mine", 0, 49238, "Grade 4 Gemdraught of Mind", 99, 4_400, true);
        var troll = new MarketSnapshot(49238, DateTimeOffset.UtcNow, [new(1, 99, true, "Troll", 2)], null);
        var decision = pricing.Evaluate(new(listing, troll, rule, null, FeeModel.Default));
        Assert.Equal(PriceDecisionKind.PriceWar, decision.Kind);
        Assert.Null(decision.TargetPrice);

        // An ordinary undercut with no history still goes through.
        var normal = new MarketSnapshot(49238, DateTimeOffset.UtcNow, [new(4_300, 99, true, "Other", 2)], null);
        var update = pricing.Evaluate(new(listing, normal, rule, null, FeeModel.Default));
        Assert.Equal(PriceDecisionKind.Update, update.Kind);
        Assert.Equal(4_299u, update.TargetPrice);
    }

    [Theory]
    [InlineData(49234u, 1_049_234u, true)]
    [InlineData(1_049_234u, 49234u, true)]
    [InlineData(49234u, 49234u, true)]
    [InlineData(49234u, 49235u, false)]
    [InlineData(0u, 0u, false)]
    [InlineData(2_000_001u, 2_000_001u, false)]
    public void PurchaseAnswersMatchWhateverQualityOffsetTheyCarry(uint first, uint second, bool same) =>
        Assert.Equal(same, ItemIdentity.SameItem(first, second));
}
