using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

/// <summary>
/// Optional market intelligence. A remote source may recommend what is worth
/// watching; it can never authorize a purchase. Discovery produces candidate
/// rules only, validated against local game data, and every candidate still has
/// to pass live prices, ROI, fees, budget and the portfolio gates before anything
/// is bought. When the source is unavailable the curated rules and the existing
/// Universalis and live-board systems carry on unchanged.
/// </summary>
public sealed class MarketDiscoveryService
{
    private readonly IMarketStatisticsProvider provider;
    private readonly Func<uint, MarketItemFacts?> lookup;
    private readonly ConfigurationService configuration;
    private readonly AutomationLog log;
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private Task? running;
    private IReadOnlyList<ProcurementRule> pending = [];
    private bool offered;

    public MarketDiscoveryService(
        IMarketStatisticsProvider provider,
        Func<uint, MarketItemFacts?> lookup,
        ConfigurationService configuration,
        AutomationLog log,
        TimeProvider? timeProvider = null)
    {
        this.provider = provider;
        this.lookup = lookup;
        this.configuration = configuration;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public string Status { get; private set; } = "Market discovery has not run yet.";

    /// <summary>
    /// True while a refresh is still in flight. <see cref="LastSuccessAt"/> is set
    /// before the task finishes, so it is not a completion signal on its own.
    /// </summary>
    public bool IsRefreshing
    {
        get { lock (sync) return running is { IsCompleted: false }; }
    }

    private TimeSpan CacheLifetime =>
        TimeSpan.FromHours(Math.Clamp(configuration.Current.MarketDiscoveryCacheHours, 1, 168));

    /// <summary>
    /// Start a refresh if the cached results have expired. Never blocks, never
    /// throws, and a failure only means the curated rules stand on their own.
    /// </summary>
    public void RefreshIfDue()
    {
        lock (sync)
        {
            if (!configuration.Current.MarketDiscoveryEnabled || !provider.IsEnabled)
                return;
            if (running is { IsCompleted: false })
                return;
            if (LastAttemptAt is { } attempted && timeProvider.GetUtcNow() - attempted < CacheLifetime)
                return;
            LastAttemptAt = timeProvider.GetUtcNow();
            // Snapshot the configuration here, on the framework thread. The rule list
            // is mutated by the controller and the dashboard; enumerating it from the
            // background task would be a race.
            var configured = configuration.Current.ProcurementRules
                .Where(x => x.ItemId != 0 && !x.DiscoveredAutomatically)
                .Select(x => x.ItemId)
                .ToHashSet();
            var region = configuration.Current.MarketDiscoveryRegion;
            running = Task.Run(() => RefreshAsync(configured, region));
        }
    }

    private async Task RefreshAsync(IReadOnlySet<uint> configured, string region)
    {
        try
        {
            var statistics = await provider
                .GetStatisticsAsync([], region, CancellationToken.None)
                .ConfigureAwait(false);
            if (statistics.Count == 0)
            {
                Status = $"{provider.Name} returned no usable statistics. Curated rules are unaffected.";
                return;
            }
            var proposals = MarketDiscoveryPolicy.Propose(statistics, lookup, configured);
            lock (sync)
                pending = proposals;
            LastSuccessAt = timeProvider.GetUtcNow();
            Status = $"{provider.Name}: {statistics.Count:N0} item statistic(s) read, " +
                     $"{proposals.Count} high-value food/medicine candidate(s) proposed.";
            log.Add(AutomationLogLevel.Information, $"MARKET DISCOVERY: {Status}");
        }
        catch (Exception ex)
        {
            // Discovery is never a hard dependency.
            Status = $"{provider.Name} was unavailable ({ex.GetType().Name}). Curated rules are unaffected.";
            log.Add(AutomationLogLevel.Warning, $"MARKET DISCOVERY: {Status}");
        }
    }

    // A short list: every line multiplies scouting cost across worlds.
    public const int MaximumAutomaticLines = 6;
    public const int MaximumAdditionsPerScan = 2;
    // Scans in a row a line may fail before it is retired to sell-off.
    public const int RetireAfterMisses = 3;

    /// <summary>
    /// Hand over proposals made elsewhere - the daily home-world sales scan - to be
    /// merged on the framework thread. An empty list still counts: it is a scan in
    /// which nothing qualified.
    /// </summary>
    public void Offer(IReadOnlyList<ProcurementRule> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        lock (sync)
        {
            pending = proposals;
            offered = true;
        }
    }

    /// <summary>
    /// Merge finished discoveries into the configuration. Called on the framework
    /// thread. Rules edited by hand are never overwritten. A discovered line that
    /// stops qualifying for several scans in a row is retired to sell-off, so stock
    /// already bought still lists from the bags; removing the rule stranded it.
    /// </summary>
    public int ApplyPendingDiscoveries()
    {
        IReadOnlyList<ProcurementRule> proposals;
        lock (sync)
        {
            if (!offered && pending.Count == 0)
                return 0;
            proposals = pending;
            pending = [];
            offered = false;
        }
        var rules = configuration.Current.ProcurementRules;
        var proposed = proposals.Select(x => x.ItemId).ToHashSet();
        var retired = 0;
        foreach (var rule in rules.Where(x => x.DiscoveredAutomatically))
        {
            if (proposed.Contains(rule.ItemId))
            {
                rule.DiscoveryMisses = 0;
                continue;
            }
            if (rule.LiquidateOnly || ++rule.DiscoveryMisses < RetireAfterMisses)
                continue;
            rule.LiquidateOnly = true;
            retired++;
            log.Add(AutomationLogLevel.Information,
                $"MARKET DISCOVERY: {rule.ItemName} has not qualified for {rule.DiscoveryMisses} scans in a row; " +
                "it is no longer bought, and what is held sells off.");
        }
        var active = rules.Count(x => x.DiscoveredAutomatically && !x.LiquidateOnly);
        var added = 0;
        foreach (var proposal in proposals)
        {
            var existing = rules.FirstOrDefault(x => x.ItemId == proposal.ItemId);
            if (existing is null)
            {
                if (added >= MaximumAdditionsPerScan || active >= MaximumAutomaticLines)
                    continue;
                rules.Add(proposal);
                added++;
                active++;
                log.Add(AutomationLogLevel.Information,
                    $"MARKET DISCOVERY: added {proposal.ItemName} as a secondary food/medicine line " +
                    $"({proposal.LastObservedSalesPerDay:N0} units a day at about {proposal.LastObservedUnitPrice:N0} gil). " +
                    "It is a candidate only: live prices, fees, demand, budget and portfolio limits still decide.");
            }
            else if (existing.DiscoveredAutomatically)
            {
                // Refresh the sizes and name from the newest statistics, but leave
                // anything the player may have changed alone.
                existing.ItemName = proposal.ItemName;
                existing.TargetStackSize = proposal.TargetStackSize;
                if (proposal.ListingStackSize > 0)
                    existing.ListingStackSize = proposal.ListingStackSize;
                if (existing.LiquidateOnly && active < MaximumAutomaticLines)
                {
                    existing.LiquidateOnly = false;
                    active++;
                }
                Reassess(existing, proposal);
            }
        }
        if (added > 0 || retired > 0)
            configuration.Save();
        return added;
    }

    /// <summary>
    /// Fold a fresh proposal into what an existing discovered rule already knows
    /// about itself, and grade how much the accumulated evidence shows.
    ///
    /// A refresh that agrees with the last one - same market, a price that has not
    /// lurched - is a confirmation. A refresh that disagrees resets the count,
    /// because the point of the count is "this line has behaved consistently", and
    /// an unstable price is exactly the evidence that it has not.
    /// </summary>
    private void Reassess(ProcurementRule existing, ProcurementRule proposal)
    {
        var config = configuration.Current;
        var spread = MarketConfidencePolicy.PriceSpreadPercent(
            existing.LastObservedUnitPrice, proposal.LastObservedUnitPrice);
        existing.DiscoveryConfirmations = spread <= MarketConfidencePolicy.ProvenMaximumPriceSpreadPercent
            ? existing.DiscoveryConfirmations + 1
            : 0;
        existing.LastObservedUnitPrice = proposal.LastObservedUnitPrice;
        existing.LastObservedSalesPerDay = proposal.LastObservedSalesPerDay;

        var stack = (uint)Math.Max(1, existing.TargetStackSize);
        var stackValue = (ulong)proposal.LastObservedUnitPrice * stack;
        var salesPerDay = proposal.LastObservedSalesPerDay;
        // The gil this line would generate at the thinnest margin the bot would
        // ever accept. Deliberately the floor rather than a hoped-for margin: it is
        // an estimate, and an estimate used to hand out the strongest tier in the
        // system should read low.
        var gilPerDay = proposal.LastObservedUnitPrice * salesPerDay *
                        config.ProcurementAbsoluteMinimumRoiPercent / 100m;
        // Evidence that the spread is real, not merely wide on paper: a purchase of
        // this item has actually cleared the margin bar and been executed.
        var realized = config.PerItemRules.TryGetValue(existing.ItemId, out var pricing) && pricing.CostBasis > 0;

        var confidence = MarketConfidencePolicy.Classify(new(
            salesPerDay, stackValue, gilPerDay, existing.DiscoveryConfirmations, spread, realized));
        // Reported, never acted on: a discovered line stays secondary unless the
        // player pins it (the 2026-11-01 decision; v43 had already un-pinned them).
        existing.Confidence = confidence;
    }

}
