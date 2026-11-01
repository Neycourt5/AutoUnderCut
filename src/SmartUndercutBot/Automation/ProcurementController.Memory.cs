using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

/// <summary>
/// How shopping uses the durable price memory. Three things only:
/// <list type="number">
/// <item>every live board read is remembered, on whatever world it was taken;</item>
/// <item>after a reload, what the last circuit saw within the configured
/// "remember away-world prices" window is restored instead of re-read;</item>
/// <item>worlds holding cheap stock in memory are scored, so the circuit opens
/// where the stock is.</item>
/// </list>
/// None of it can buy anything. Purchases still need the live board, a fresh home
/// price and every guard; a remembered row only decides where to look.
/// </summary>
public sealed partial class ProcurementController
{
    /// <summary>How old cached (Universalis) readings may be to steer a route.</summary>
    private static readonly TimeSpan CachedRouteEvidenceAge = TimeSpan.FromHours(3);

    /// <summary>Remember a live board reading of one item on the current world.</summary>
    private void RememberLiveRead(uint itemId, IReadOnlyList<ProcurementMarketListing> rows, bool atHome, bool boardEmpty)
    {
        if (priceMemory?.Invoke() is not { } memory || boardEmpty || string.IsNullOrWhiteSpace(WorldName))
            return;
        var rule = configuration.Current.ProcurementRules.FirstOrDefault(x => x.ItemId == itemId && x.Enabled);
        var now = timeProvider.GetUtcNow();
        var owned = retainerListings.OwnedRetainerIds;
        foreach (var quality in new[] { false, true })
        {
            if (rule is not null && !ResaleStockPolicy.BuyableQuality(rule, quality, configuration.Current.BuyHighQualityOnly))
                continue;
            var competing = rows.Where(x => x.IsHighQuality == quality && !owned.Contains(x.RetainerId)).ToArray();
            memory.RecordWorld(WorldName, itemId, quality, PriceSource.LiveBoard, now, competing);
            if (!atHome || competing.Length == 0)
                continue;
            var sales = priorityDemand.FirstOrDefault(x => x.ItemId == itemId)?.RecentSales
                .Where(s => s.IsHighQuality == quality && s.PricePerUnit > 0 && s.Quantity > 0 &&
                            s.SoldAt >= now.AddDays(-7) && s.SoldAt <= now)
                .ToArray() ?? [];
            var spanDays = sales.Length == 0 ? 0m : (decimal)Math.Max(1d / 24d, (now - sales.Min(s => s.SoldAt)).TotalDays);
            memory.RecordHome(new(itemId, quality, PriceSource.LiveBoard, now,
                competing.Min(x => x.PricePerUnit), competing.Length,
                competing.Aggregate(0UL, (sum, x) => sum + x.Quantity),
                HomeSalesPerDay(itemId, quality), spanDays > 0 ? sales.Length / spanDays : 0m,
                Median(sales.Select(s => s.PricePerUnit)), Median(sales.Select(s => s.Quantity))));
        }
    }

    /// <summary>
    /// Put remembered live readings back into the circuit's knowledge after a
    /// reload, within the same lifetime the circuit itself honours. Only live board
    /// readings qualify, because only they describe rows the planner may compare.
    /// </summary>
    private void RestoreScoutKnowledge()
    {
        if (priceMemory?.Invoke() is not { } memory)
            return;
        var now = timeProvider.GetUtcNow();
        var restored = 0;
        var items = stockHuntRules.Select(x => x.ItemId).ToHashSet();
        foreach (var snapshot in memory.AllFresh(now, ScoutKnowledgeLife, PriceSource.LiveBoard))
        {
            if (!items.Contains(snapshot.ItemId) ||
                snapshot.World.Equals(homeWorld, StringComparison.OrdinalIgnoreCase) ||
                !NorthAmericaWorlds.Contains(snapshot.World, StringComparer.OrdinalIgnoreCase) ||
                scoutObservedAt.ContainsKey((snapshot.World, snapshot.ItemId)) ||
                scoutListings.Any(x => x.ItemId == snapshot.ItemId && x.IsHighQuality == snapshot.IsHighQuality &&
                                       x.WorldName.Equals(snapshot.World, StringComparison.OrdinalIgnoreCase)))
                continue;
            var world = NorthAmericaWorlds.First(w => w.Equals(snapshot.World, StringComparison.OrdinalIgnoreCase));
            scoutListings.AddRange(snapshot.Cheapest.Select(x => new ProcurementMarketListing(snapshot.ItemId,
                x.ListingId, x.RetainerId, world, 0, x.PricePerUnit, x.Quantity, snapshot.IsHighQuality)));
            // Stamp the reading's own time, so it ages out exactly as if it had
            // never left memory.
            if (!scoutObservedAt.TryGetValue((world, snapshot.ItemId), out var have) || have < snapshot.ObservedAt)
                scoutObservedAt[(world, snapshot.ItemId)] = snapshot.ObservedAt;
            restored++;
        }
        if (restored > 0)
            log.Add(AutomationLogLevel.Information,
                $"PRICE MEMORY: restored {restored} live price reading(s) younger than {ScoutKnowledgeLife.TotalHours:N0}h " +
                "from before the reload; those boards are not re-read this circuit.");
    }

    /// <summary>
    /// Expected profit sitting on each world according to memory: for every traded
    /// line, the remembered rows below what the home market would pay, after both
    /// taxes. Live readings count within the scout lifetime, cached ones for three
    /// hours. Used only to order the route.
    /// </summary>
    private Dictionary<string, decimal> MemoryRouteScores()
    {
        var scores = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (priceMemory?.Invoke() is not { } memory)
            return scores;
        var config = configuration.Current;
        var fees = config.Fees;
        var now = timeProvider.GetUtcNow();
        foreach (var rule in stockHuntRules)
        {
            foreach (var quality in new[] { false, true })
            {
                if (!ResaleStockPolicy.BuyableQuality(rule, quality, config.BuyHighQualityOnly))
                    continue;
                var reference = RouteReferencePrice(memory, rule, quality, now);
                if (reference == 0)
                    continue;
                var stack = (uint)Math.Max(1, rule.TargetStackSize);
                foreach (var snapshot in memory.FreshWorlds(rule.ItemId, quality, now,
                             ScoutKnowledgeLife > CachedRouteEvidenceAge ? ScoutKnowledgeLife : CachedRouteEvidenceAge))
                {
                    if (snapshot.Source != PriceSource.LiveBoard && now - snapshot.ObservedAt > CachedRouteEvidenceAge ||
                        snapshot.World.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Count at most a few stacks per world: depth beyond what one
                    // trip can absorb does not make a world more worth visiting.
                    var units = 0UL;
                    var score = 0m;
                    foreach (var row in snapshot.Cheapest)
                    {
                        var margin = fees.NetUnitProceeds(reference) - (decimal)fees.LandedUnitCost(row.PricePerUnit);
                        if (margin <= 0 || units >= stack * 4UL)
                            break;
                        var take = Math.Min(row.Quantity, stack * 4UL - units);
                        units += take;
                        score += margin * take;
                    }
                    if (score > 0)
                        scores[snapshot.World] = scores.GetValueOrDefault(snapshot.World) + score;
                }
            }
        }
        return scores;
    }

    /// <summary>
    /// The home price a remembered row is scored against: the live home reference
    /// when shopping has one, otherwise the lower of the remembered floor and the
    /// remembered median sale price, so a route is never steered by an optimistic
    /// resale price.
    /// </summary>
    private uint RouteReferencePrice(PriceMemory memory, ProcurementRule rule, bool quality, DateTimeOffset now)
    {
        if (homePrices.TryGetValue(rule.ItemId, out var listings) && listings.Length > 0)
        {
            var reference = HomePriceReference.Summarize(rule.ItemId, rule.ItemName, quality, listings
                .Where(x => !retainerListings.OwnedRetainerIds.Contains(x.RetainerId)).ToArray()).Reference;
            if (reference > 0)
                return reference;
        }
        if (memory.LatestHome(rule.ItemId, quality, now, TimeSpan.FromHours(6)) is not { } home)
            return 0;
        var prices = new[] { home.LowestCompetitor, home.MedianSalePrice }.Where(x => x > 0).ToArray();
        return prices.Length == 0 ? 0 : prices.Min();
    }

    private static uint Median(IEnumerable<uint> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (uint)(((ulong)sorted[middle - 1] + sorted[middle]) / 2);
    }
}
