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

    /// <summary>
    /// Merge finished discoveries into the configuration. Called on the framework
    /// thread. Rules edited by hand are never overwritten, and a discovery that
    /// stops qualifying is retired rather than left behind forever.
    /// </summary>
    public int ApplyPendingDiscoveries()
    {
        IReadOnlyList<ProcurementRule> proposals;
        lock (sync)
        {
            if (pending.Count == 0)
                return 0;
            proposals = pending;
            pending = [];
        }
        var rules = configuration.Current.ProcurementRules;
        var proposed = proposals.Select(x => x.ItemId).ToHashSet();
        var retired = rules.RemoveAll(x => x.DiscoveredAutomatically && !proposed.Contains(x.ItemId));
        var added = 0;
        var promoted = 0;
        foreach (var proposal in proposals)
        {
            var existing = rules.FirstOrDefault(x => x.ItemId == proposal.ItemId);
            if (existing is null)
            {
                rules.Add(proposal);
                added++;
            }
            else if (existing.DiscoveredAutomatically)
            {
                // Refresh the stack size and name from the newest statistics, but
                // leave anything the player may have changed alone.
                existing.ItemName = proposal.ItemName;
                existing.TargetStackSize = proposal.TargetStackSize;
                promoted += Reassess(existing, proposal) ? 1 : 0;
            }
        }
        if (added > 0 || retired > 0 || promoted > 0)
        {
            configuration.Save();
            log.Add(AutomationLogLevel.Information,
                $"MARKET DISCOVERY: added {added}, retired {retired} and promoted {promoted} automatically " +
                "discovered flip(s). Additions are candidates only: live prices, fees, demand, budget and " +
                "portfolio limits still decide.");
        }
        return added;
    }

    /// <summary>
    /// Fold a fresh proposal into what an existing discovered rule already knows
    /// about itself, and promote it only if the accumulated evidence justifies it.
    ///
    /// A refresh that agrees with the last one - same market, a price that has not
    /// lurched - is a confirmation. A refresh that disagrees resets the count,
    /// because the point of the count is "this line has behaved consistently", and
    /// an unstable price is exactly the evidence that it has not.
    /// </summary>
    private bool Reassess(ProcurementRule existing, ProcurementRule proposal)
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
        var wasPinned = existing.PreferredStock;
        existing.Confidence = confidence;
        existing.PreferredStock = MarketConfidencePolicy.ShouldPin(confidence);
        if (!existing.PreferredStock || wasPinned)
            return false;
        log.Add(AutomationLogLevel.Information,
            $"MARKET DISCOVERY: {existing.ItemName} promoted to preferred core stock after " +
            $"{existing.DiscoveryConfirmations} agreeing observation(s) at {salesPerDay:N0} units/day, " +
            $"{stackValue:N0} gil per stack and a realised profitable spread.");
        return true;
    }

}
