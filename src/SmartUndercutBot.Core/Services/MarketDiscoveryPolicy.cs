using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// Turns cached market statistics into candidate trading rules. Discovery only
/// ever proposes what is worth watching; the six curated consumables stay pinned
/// whether or not any external source answers, and every proposed rule still has
/// to survive live prices, ROI, budget and portfolio gates before anything is
/// bought.
/// </summary>
public static class MarketDiscoveryPolicy
{
    /// <summary>Food and medicine only, and only if it is worth a retainer slot.</summary>
    public const uint MinimumStackValue = 150_000;
    public const decimal MinimumUnitsSoldPerDay = 50m;

    /// <summary>
    /// Keep the discovered list short. Every extra item multiplies scouting cost
    /// across worlds, and the point is a focused portfolio, not a long tail.
    /// </summary>
    public const int MaximumDiscoveredRules = 12;

    /// <summary>
    /// How many items may be looked up in the game's own data. A whole-region
    /// dataset is around seventeen thousand items, and resolving every one of them
    /// means that many Excel sheet reads off the framework thread for the sake of a
    /// dozen results. Rank on the numbers first, which needs no game data at all,
    /// and only inspect the plausible head of the list.
    /// </summary>
    public const int MaximumItemLookups = 500;

    public static IReadOnlyList<ProcurementRule> Propose(
        IReadOnlyList<MarketStatistic> statistics,
        Func<uint, MarketItemFacts?> lookup,
        IReadOnlySet<uint> alreadyConfigured,
        int maximumRules = MaximumDiscoveredRules,
        int maximumLookups = MaximumItemLookups)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(alreadyConfigured);

        var shortlist = statistics
            .Where(x => x.ItemId != 0 && !alreadyConfigured.Contains(x.ItemId) &&
                        x.BestPrice > 0 && x.BestUnitsSoldPerDay >= MinimumUnitsSoldPerDay)
            .OrderByDescending(x => x.BestPrice * x.BestUnitsSoldPerDay)
            .Take(Math.Max(0, maximumLookups))
            .ToArray();

        var proposals = new List<(ProcurementRule Rule, decimal Score)>();
        foreach (var statistic in shortlist)
        {
            // Local game data is the authority on what the item actually is. A
            // remote source may name an untradable, non-existent or wrong-category
            // id, and none of those may become a buy candidate.
            if (lookup(statistic.ItemId) is not { IsTradable: true, IsFoodOrMedicine: true } facts)
                continue;

            var highQuality = facts.CanBeHighQuality && statistic.MedianHqPrice > 0;
            var price = highQuality ? statistic.MedianHqPrice : statistic.MedianNqPrice;
            var perDay = highQuality ? statistic.HqUnitsSoldPerDay : statistic.NqUnitsSoldPerDay;
            var stack = Math.Clamp(facts.StackSize, 1, 999);
            if (price == 0 || (ulong)price * (ulong)stack < MinimumStackValue || perDay < MinimumUnitsSoldPerDay)
                continue;

            proposals.Add((new ProcurementRule
            {
                ItemId = statistic.ItemId,
                ItemName = string.IsNullOrWhiteSpace(facts.Name) ? statistic.ItemName : facts.Name,
                TargetStackSize = stack,
                MaximumSaleSlots = 4,
                MinimumWeeklyUnitsSold = 20,
                AllowHighQuality = facts.CanBeHighQuality,
                RequireHighQuality = highQuality,
                ListFromBags = true,
                HuntOnTour = true,
                TourPriority = 0,
                // Objectively high-value, high-volume food or medicine: exactly the
                // kind of stock the curated list exists to hold.
                PreferredStock = true,
                DiscoveredAutomatically = true,
            }, price * (decimal)stack * perDay));
        }

        return proposals
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(0, maximumRules))
            .Select(x => x.Rule)
            .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
