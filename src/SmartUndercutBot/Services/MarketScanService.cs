using System.Text.Json;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

/// <summary>
/// Raw market data the scanner asks for. Every member returns null when its source
/// does not answer; parsing lives in Core so it can be tested against real shapes.
/// </summary>
public interface IMarketScanSource
{
    /// <summary>The cheapest listings of one quality across a whole region (Universalis current data).</summary>
    Task<JsonDocument?> RegionListingsAsync(IReadOnlyList<uint> itemIds, string region, bool highQuality, int listings,
        CancellationToken token);

    /// <summary>One world's listings and last week of sales for one quality (Universalis current data).</summary>
    Task<JsonDocument?> WorldMarketAsync(IReadOnlyList<uint> itemIds, string world, bool highQuality, CancellationToken token);

    /// <summary>Universalis' cached aggregate: cheapest listing per world, data centre and region. At most 100 ids.</summary>
    Task<JsonDocument?> AggregatedAsync(IReadOnlyList<uint> itemIds, string scope, CancellationToken token);

    /// <summary>Saddlebag Exchange's ranking of what sold on one world over a window.</summary>
    Task<JsonDocument?> MarketshareAsync(string world, int hours, int minimumSales, int minimumAveragePrice,
        IReadOnlyList<int> filters, string sortBy, CancellationToken token);
}

/// <summary>
/// Keeps the price memory current without anyone travelling.
///
/// Every <see cref="Configuration.MarketScanIntervalMinutes"/> it reads, for each
/// traded item, the cheapest listings across North America (where the cheap stock
/// is, dated by each world's last upload) and the home market (floor, depth, a
/// week of sales). About two requests per twenty items. Once a day it asks
/// Saddlebag what actually sold on the home world, and Universalis where each of
/// those items is cheapest, so the dashboard can show what else is worth trading.
///
/// All of it is intelligence. Shopping still reads every board live and checks
/// every profit guard before a purchase; a source that is down only means older
/// memory. One scan runs at a time and nothing here ever throws.
/// </summary>
public sealed class MarketScanService : IDisposable
{
    public const string Region = "North-America";
    public const int RegionListings = 100;
    public const int ItemsPerRequest = 20;
    public const int DiscoveryWindowHours = 168;
    public const int DiscoveryMinimumWeeklySales = 20;
    public const int DiscoveryMinimumAveragePrice = 300;
    public const int MaximumCandidates = 150;

    private readonly IMarketScanSource source;
    private readonly ConfigurationService configuration;
    private readonly Func<PriceMemory> memory;
    private readonly Func<string?> homeWorld;
    private readonly Func<IReadOnlySet<string>> ownRetainerNames;
    private readonly Func<IReadOnlySet<ulong>> ownRetainerIds;
    private readonly Func<uint, MarketItemFacts?> lookup;
    private readonly Func<uint, string?> worldName;
    // Food and medicine the daily ranking qualifies, handed to the discovery merge.
    private readonly Action<IReadOnlyList<ProcurementRule>>? offerLines;
    private readonly AutomationLog log;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource stopping = new();
    private readonly object sync = new();
    private Task? running;
    private DateTimeOffset nextTrackedScan = DateTimeOffset.MinValue;
    private DateTimeOffset nextDiscoveryScan = DateTimeOffset.MinValue;

    public MarketScanService(
        IMarketScanSource source,
        ConfigurationService configuration,
        Func<PriceMemory> memory,
        Func<string?> homeWorld,
        Func<IReadOnlySet<string>> ownRetainerNames,
        Func<IReadOnlySet<ulong>> ownRetainerIds,
        Func<uint, MarketItemFacts?> lookup,
        Func<uint, string?> worldName,
        AutomationLog log,
        TimeProvider? timeProvider = null,
        Action<IReadOnlyList<ProcurementRule>>? offerLines = null)
    {
        this.source = source;
        this.configuration = configuration;
        this.memory = memory;
        this.homeWorld = homeWorld;
        this.ownRetainerNames = ownRetainerNames;
        this.ownRetainerIds = ownRetainerIds;
        this.lookup = lookup;
        this.worldName = worldName;
        this.offerLines = offerLines;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset? LastTrackedScanAt { get; private set; }
    public DateTimeOffset? LastDiscoveryScanAt { get; private set; }
    public DateTimeOffset NextTrackedScanAt => nextTrackedScan;
    public string Status { get; private set; } = "Market scans have not run yet.";

    public bool IsScanning
    {
        get { lock (sync) return running is { IsCompleted: false }; }
    }

    private TimeSpan TrackedInterval =>
        TimeSpan.FromMinutes(Math.Clamp(configuration.Current.MarketScanIntervalMinutes, 10, 240));

    private TimeSpan DiscoveryInterval =>
        TimeSpan.FromHours(Math.Clamp(configuration.Current.MarketDiscoveryScanHours, 6, 168));

    /// <summary>Call on the framework thread. Starts whichever scan is due; never blocks.</summary>
    public void Update(bool force = false)
    {
        var config = configuration.Current;
        if (!config.MarketScanEnabled && !force)
            return;
        var home = homeWorld();
        if (string.IsNullOrWhiteSpace(home))
            return;
        var now = timeProvider.GetUtcNow();
        lock (sync)
        {
            if (running is { IsCompleted: false })
                return;
            var tracked = force || now >= nextTrackedScan;
            var discovery = force || now >= nextDiscoveryScan &&
                (memory().CandidatesObservedAt is not { } scanned || now - scanned >= DiscoveryInterval);
            if (!tracked && !discovery)
                return;
            // Snapshot everything the background task needs here, on the framework
            // thread: the rule list is edited by the controllers and the dashboard.
            var items = TrackedItems(config);
            var names = ownRetainerNames();
            var ids = ownRetainerIds();
            // Lines the player already has a rule for, discovered ones aside: those
            // may be proposed again, which is what keeps them from being retired.
            var configured = config.ProcurementRules.Where(x => x.ItemId != 0 && !x.DiscoveredAutomatically)
                .Select(x => x.ItemId).ToHashSet();
            var fees = config.Fees;
            if (tracked)
                nextTrackedScan = now + TrackedInterval;
            if (discovery)
                nextDiscoveryScan = now + DiscoveryInterval;
            var target = memory();
            running = Task.Run(() => RunAsync(target, home!, items, names, ids, tracked, discovery,
                new(configured, fees), stopping.Token));
        }
    }

    /// <summary>The traded lines: every enabled, buyable rule, in each quality it may be bought.</summary>
    public static IReadOnlyList<(uint ItemId, bool HighQuality)> TrackedItems(Configuration config) =>
        config.ProcurementRules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .DistinctBy(x => x.ItemId)
            .OrderByDescending(x => x.PreferredStock || x.AlwaysScout)
            .ThenBy(x => x.TourPriority)
            .SelectMany(rule => new[] { true, false }
                .Where(q => ResaleStockPolicy.BuyableQuality(rule, q, config.BuyHighQualityOnly))
                .Select(q => (rule.ItemId, q)))
            .Take(120)
            .ToArray();

    private sealed record DiscoveryContext(IReadOnlySet<uint> Configured, FeeModel Fees);

    private async Task RunAsync(PriceMemory target, string home, IReadOnlyList<(uint ItemId, bool HighQuality)> items,
        IReadOnlySet<string> names, IReadOnlySet<ulong> ids, bool tracked, bool discovery, DiscoveryContext context,
        CancellationToken token)
    {
        var parts = new List<string>();
        try
        {
            if (tracked)
                parts.Add(await ScanTrackedAsync(target, home, items, names, ids, token).ConfigureAwait(false));
            if (discovery)
                parts.Add(await ScanDiscoveryAsync(target, home, context, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            parts.Add($"stopped by {ex.GetType().Name}: {ex.Message}");
        }
        Status = "MARKET SCAN: " + string.Join(" ", parts.Where(x => x.Length > 0));
        log.Add(AutomationLogLevel.Information, Status);
    }

    private async Task<string> ScanTrackedAsync(PriceMemory target, string home,
        IReadOnlyList<(uint ItemId, bool HighQuality)> items, IReadOnlySet<string> names, IReadOnlySet<ulong> ids,
        CancellationToken token)
    {
        if (items.Count == 0)
            return "No traded lines to price.";
        int worlds = 0, homes = 0, failed = 0, requests = 0;
        foreach (var quality in new[] { true, false })
        {
            foreach (var batch in items.Where(x => x.HighQuality == quality).Select(x => x.ItemId).Distinct()
                         .Chunk(ItemsPerRequest))
            {
                token.ThrowIfCancellationRequested();
                requests += 2;
                var fetchedAt = timeProvider.GetUtcNow();
                using (var region = await source.RegionListingsAsync(batch, Region, quality, RegionListings, token)
                           .ConfigureAwait(false))
                {
                    if (region is null)
                        failed++;
                    else
                        foreach (var snapshot in UniversalisMemoryParser.ParseWorldSnapshots(
                                     region.RootElement, quality, home, RegionListings, fetchedAt))
                        {
                            target.RecordWorld(snapshot);
                            worlds++;
                        }
                }
                using (var market = await source.WorldMarketAsync(batch, home, quality, token).ConfigureAwait(false))
                {
                    if (market is null)
                        failed++;
                    else
                        foreach (var snapshot in UniversalisMemoryParser.ParseHomeSnapshots(
                                     market.RootElement, quality, names, timeProvider.GetUtcNow(), ids))
                        {
                            target.RecordHome(snapshot);
                            homes++;
                        }
                }
            }
        }
        if (failed < requests)
            LastTrackedScanAt = timeProvider.GetUtcNow();
        return $"{items.Count} traded line(s): {worlds} cheapest-world reading(s) across {Region}, " +
               $"{homes} {home} market reading(s)" +
               (failed > 0 ? $"; {failed} of {requests} request(s) went unanswered." : ".");
    }

    private async Task<string> ScanDiscoveryAsync(PriceMemory target, string home, DiscoveryContext context,
        CancellationToken token)
    {
        using var ranking = await source.MarketshareAsync(home, DiscoveryWindowHours, DiscoveryMinimumWeeklySales,
            DiscoveryMinimumAveragePrice, [0], "marketValue", token).ConfigureAwait(false);
        if (ranking is null)
            return "Saddlebag's sales ranking did not answer; the last scan's candidates stand.";
        var observedAt = timeProvider.GetUtcNow();
        var rows = SaddlebagParser.ParseMarketshare(ranking.RootElement)
            .Where(x => x.Sales >= DiscoveryMinimumWeeklySales && x.UnitsSold > 0)
            .OrderByDescending(x => x.MarketValue)
            .ToArray();
        var candidates = new List<MarketCandidate>();
        foreach (var row in rows)
        {
            if (candidates.Count >= MaximumCandidates)
                break;
            // Local game data is the authority on what an item is: a remote id that
            // is untradable or unknown never becomes a candidate.
            if (lookup(row.ItemId) is not { IsTradable: true } facts)
                continue;
            candidates.Add(SaddlebagParser.ToCandidate(row, DiscoveryWindowHours, facts.CanBeHighQuality,
                string.IsNullOrWhiteSpace(facts.Category) ? "Other" : facts.Category, observedAt) with
            {
                ItemName = string.IsNullOrWhiteSpace(facts.Name) ? row.Name : facts.Name,
            });
        }
        // Where each candidate is cheapest elsewhere, from the cached aggregate.
        var cheapest = new Dictionary<(uint, bool), (uint Price, string World)>();
        var unanswered = 0;
        foreach (var batch in candidates.Select(x => x.ItemId).Distinct().Chunk(100))
        {
            token.ThrowIfCancellationRequested();
            using var aggregate = await source.AggregatedAsync(batch, home, token).ConfigureAwait(false);
            if (aggregate is null)
            {
                unanswered++;
                continue;
            }
            foreach (var hint in UniversalisAggregatedParser.ParseHints(aggregate.RootElement, worldName))
            {
                if (hint.WorldName.Equals(home, StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = (hint.ItemId, hint.IsHighQuality);
                if (!cheapest.TryGetValue(key, out var have) || hint.PricePerUnit < have.Price)
                    cheapest[key] = (hint.PricePerUnit, hint.WorldName);
            }
        }
        var priced = candidates.Select(c => cheapest.TryGetValue((c.ItemId, c.IsHighQuality), out var at)
                ? c with { CheapestElsewhere = at.Price, CheapestWorld = at.World }
                : c)
            .OrderByDescending(x => x.HomeGilPerDay)
            .ToArray();
        target.ReplaceCandidates(priced, observedAt);
        LastDiscoveryScanAt = observedAt;
        // Only food and medicine are ever added as lines; the rest stays a suggestion.
        var lines = MarketDiscoveryPolicy.ProposeFromHomeRanking(priced, lookup, context.Configured, context.Fees);
        // Without every spread a line that still qualifies would look as if it had
        // stopped, so an incomplete scan neither adds nor counts against anything.
        if (unanswered == 0)
            offerLines?.Invoke(lines);
        return $"Saddlebag ranked {rows.Length} {home} market(s) with {DiscoveryMinimumWeeklySales}+ sales a week; " +
               $"{priced.Length} tradeable candidate(s) kept, {lines.Count} food/medicine line(s) qualify to be added" +
               (unanswered > 0 ? $", {unanswered} spread request(s) unanswered." : ".");
    }

    public void Dispose()
    {
        stopping.Cancel();
        Task? pending;
        lock (sync)
            pending = running;
        try { pending?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        stopping.Dispose();
    }
}
