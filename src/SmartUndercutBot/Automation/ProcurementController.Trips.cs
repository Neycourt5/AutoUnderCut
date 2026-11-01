using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

/// <summary>A world the price memory says is worth the trip, and the lines that make it so.</summary>
public sealed record TripTarget(string World, decimal ExpectedProfit, IReadOnlyList<uint> Items);

/// <summary>
/// Targeted trips. A full circuit of every away world took about 100 minutes, nearly
/// all of it travel, with repricing paused the whole time. The price memory already
/// knows where the cheap stock is: every traded line's cheapest listings across the
/// region every half hour, and every board the bot has read. So a trip goes straight
/// to the few worlds that memory shows holding stock cheap enough to clear the margin
/// bars, and comes back to undercut. A full circuit still runs, at most every few
/// hours, when memory knows too few such worlds to plan a trip on.
/// </summary>
public sealed partial class ProcurementController
{
    private bool targetedTrip;
    // Set by an explicit "shop now". With nothing in memory worth a trip, an
    // explicit request still travels, on a full circuit.
    private bool manualTripRequested;
    private IReadOnlyList<TripTarget>? targetCache;
    private DateTimeOffset targetCacheAt;

    /// <summary>Whether the current or last trip was a targeted one.</summary>
    public bool IsTargetedTrip => targetedTrip;

    private bool FullCircuitDue => configuration.Current.LastFullCircuitAt is not { } last ||
        timeProvider.GetUtcNow() - last >= TimeSpan.FromHours(Math.Max(1, configuration.Current.FullCircuitIntervalHours));

    public DateTimeOffset? NextFullCircuitAllowedAt => configuration.Current.LastFullCircuitAt is { } last
        ? last.AddHours(Math.Max(1, configuration.Current.FullCircuitIntervalHours))
        : null;

    /// <summary>
    /// Worlds whose remembered listings, at today's home price, carry at least one
    /// lot's worth of profit at the margin each line has to clear. Best first.
    /// Cached briefly: the dashboard and the scheduler ask on every frame.
    /// </summary>
    public IReadOnlyList<TripTarget> TripTargets(IReadOnlyList<ProcurementRule>? rules = null)
    {
        var now = timeProvider.GetUtcNow();
        if (rules is null && targetCache is not null && now - targetCacheAt < TimeSpan.FromMinutes(1))
            return targetCache;
        var targets = FindTripTargets(rules ?? configuration.Current.ProcurementRules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly).DistinctBy(x => x.ItemId).ToArray(), now);
        if (rules is null)
        {
            targetCache = targets;
            targetCacheAt = now;
        }
        return targets;
    }

    private IReadOnlyList<TripTarget> FindTripTargets(IReadOnlyList<ProcurementRule> rules, DateTimeOffset now)
    {
        var config = configuration.Current;
        // Asked before any trip has resolved the home world, so resolve it here:
        // the home board's own rows must never look like an away bargain.
        var home = TryGetHomeWorld(out var resolved) ? resolved : homeWorld;
        var found = new Dictionary<string, (decimal Profit, HashSet<uint> Items)>(StringComparer.OrdinalIgnoreCase);
        void Add(string world, decimal profit, uint item)
        {
            var entry = found.TryGetValue(world, out var have) ? have : (0m, new HashSet<uint>());
            entry.Item2.Add(item);
            found[world] = (entry.Item1 + profit, entry.Item2);
        }

        var lotProfit = (decimal)Math.Max(1u, config.ProcurementMinimumProfitPerSaleSlot);
        if (priceMemory?.Invoke() is { } memory)
        {
            var fees = config.Fees;
            var economics = Economics;
            var maximumAge = ScoutKnowledgeLife > CachedRouteEvidenceAge ? ScoutKnowledgeLife : CachedRouteEvidenceAge;
            foreach (var rule in rules)
            {
                foreach (var quality in new[] { false, true })
                {
                    if (!ResaleStockPolicy.BuyableQuality(rule, quality, config.BuyHighQualityOnly))
                        continue;
                    var reference = RouteReferencePrice(memory, rule, quality, now);
                    if (reference == 0)
                        continue;
                    var stack = (uint)Math.Max(1, rule.TargetStackSize);
                    var velocity = HomeSalesPerDay(rule.ItemId, quality) is > 0 and var live
                        ? live
                        : memory.LatestHome(rule.ItemId, quality, now, TimeSpan.FromHours(6))?.UnitsPerDay ?? 0m;
                    var required = PortfolioPolicy.RequiredRoiPercent(economics,
                        rule.PreferredStock || rule.Consumable, velocity, (ulong)reference * stack);
                    var net = (decimal)fees.NetUnitProceeds(reference);
                    foreach (var snapshot in memory.FreshWorlds(rule.ItemId, quality, now, maximumAge))
                    {
                        if (snapshot.Source != PriceSource.LiveBoard && now - snapshot.ObservedAt > CachedRouteEvidenceAge ||
                            snapshot.World.Equals(home, StringComparison.OrdinalIgnoreCase) ||
                            !NorthAmericaWorlds.Contains(snapshot.World, StringComparer.OrdinalIgnoreCase) ||
                            !ProcurementTravelPolicy.CanShopOnWorld(snapshot.World))
                            continue;
                        // A few lots at most per line: depth beyond what one trip can
                        // absorb does not make a world more worth the visit.
                        var units = 0UL;
                        var profit = 0m;
                        foreach (var row in snapshot.Cheapest)
                        {
                            var landed = (decimal)fees.LandedUnitCost(row.PricePerUnit);
                            if (landed <= 0 || units >= stack * 4UL ||
                                (net - landed) / landed * 100m < required)
                                break;
                            var take = Math.Min(row.Quantity, stack * 4UL - units);
                            units += take;
                            profit += (net - landed) * take;
                        }
                        if (profit > 0)
                            Add(NorthAmericaWorlds.First(w => w.Equals(snapshot.World, StringComparison.OrdinalIgnoreCase)),
                                profit, rule.ItemId);
                    }
                }
            }
        }
        // A market still selling cheaply when buying last stopped is evidence too.
        foreach (var bargain in BargainMemoryPolicy.Fresh(config.RememberedBargains, now, ScoutKnowledgeLife))
            if (!bargain.WorldName.Equals(home, StringComparison.OrdinalIgnoreCase) &&
                ProcurementTravelPolicy.CanShopOnWorld(bargain.WorldName) &&
                rules.Any(x => x.ItemId == bargain.ItemId))
                Add(bargain.WorldName, lotProfit, bargain.ItemId);

        return found
            .Where(x => x.Value.Profit >= lotProfit)
            .OrderByDescending(x => x.Value.Profit)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(config.TargetedTripMaximumWorlds, 1, 20))
            .Select(x => new TripTarget(x.Key, x.Value.Profit, x.Value.Items.ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Why a scheduled trip is not worth starting: memory knows no world worth
    /// visiting and the last full circuit was too recent to run another. Null when
    /// a trip should go ahead. An explicit "shop now" is never held by this.
    /// </summary>
    private string? TargetedTripWaitReason
    {
        get
        {
            var config = configuration.Current;
            if (!config.TargetedTripsEnabled || !config.PriorityShoppingEnabled || FullCircuitDue ||
                TripTargets().Count > 0)
                return null;
            return "no remembered world holds stock cheap enough to clear the margin bars; the next full " +
                   $"circuit may run at {NextFullCircuitAllowedAt?.LocalDateTime:t}";
        }
    }

    /// <summary>
    /// Lay out a targeted trip: home, then the target worlds in circuit order (own
    /// data center first, each data center finished before the next), each priced
    /// for the food block, the lines that made it a target, and its cached hints.
    /// The full circuit's saved route and cursor are left exactly as they were.
    /// </summary>
    private void PrepareTargetedRoute(IReadOnlyList<TripTarget> targets,
        IReadOnlyList<(MarketPriceHint Hint, decimal Score)> hints, IReadOnlyList<BargainMemory> remembered,
        IReadOnlyList<uint> preferredItems, IReadOnlyList<uint> secondaryItems)
    {
        var config = configuration.Current;
        var scores = targets.ToDictionary(x => x.World, x => x.ExpectedProfit, StringComparer.OrdinalIgnoreCase);
        var revisit = BargainMemoryPolicy.RevisitOrder(remembered).Where(scores.ContainsKey).ToArray();
        var ordered = ShoppingScoutPolicy.BuildRoute(NorthAmericaWorlds, homeWorld, scores, revisit)
            .Where(scores.ContainsKey).ToArray();
        targetedTrip = true;
        stockHuntWorlds = [homeWorld, .. ordered];
        scoutItems.Clear();
        var limit = Math.Max(1, config.PriorityItemsPerWorld);
        for (var i = 0; i < ordered.Length; i++)
        {
            var world = ordered[i];
            var evidence = targets.First(x => x.World.Equals(world, StringComparison.OrdinalIgnoreCase)).Items
                .Concat(BargainMemoryPolicy.RevisitItems(remembered, world));
            var hinted = hints.Where(x => x.Hint.WorldName.Equals(world, StringComparison.OrdinalIgnoreCase) && x.Score > 0)
                .OrderByDescending(x => x.Score).Select(x => x.Hint.ItemId).Distinct().Take(Math.Max(1, limit / 4));
            scoutItems[world] = ShoppingScoutPolicy
                .SelectWorldItems(preferredItems, secondaryItems, evidence, hinted, i, limit).ToHashSet();
        }
        log.Add(AutomationLogLevel.Information,
            $"TARGETED TRIP: memory shows stock cheap enough to buy on {ordered.Length} world(s): " +
            string.Join(", ", ordered.Select(w => $"{w} (~{scores[w]:N0} gil)")) +
            $". Back to the retainers within about {config.TargetedTripMinutes} minutes" +
            (NextFullCircuitAllowedAt is { } next && next > timeProvider.GetUtcNow()
                ? $"; the next full circuit may run at {next.LocalDateTime:t}."
                : "."));
    }
}
