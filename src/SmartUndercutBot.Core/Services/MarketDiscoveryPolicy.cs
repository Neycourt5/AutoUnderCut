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
    public const ulong MinimumStackValue = MarketConfidencePolicy.CandidateMinimumStackValue;
    public const decimal MinimumUnitsSoldPerDay = MarketConfidencePolicy.CandidateMinimumSalesPerDay;

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
                TourPriority = 3,
                // A proposal is a candidate, never core stock. One flattering
                // statistics call is not evidence that a market behaves like
                // popcorn; MarketConfidencePolicy decides that, over several
                // refreshes that agree with each other.
                PreferredStock = false,
                Confidence = MarketConfidence.Candidate,
                DiscoveredAutomatically = true,
                LastObservedUnitPrice = price,
                LastObservedSalesPerDay = perDay,
            }, price * (decimal)stack * perDay));
        }

        return proposals
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(0, maximumRules))
            .Select(x => x.Rule)
            .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // The home ranking's cheapest-elsewhere price is a single listing, which
    // overstates how much can be bought at it, so the bar sits above the 20% food bar.
    public const decimal HomeRankingMinimumNetRoiPercent = 30m;
    public const decimal HomeRankingMinimumSalesPerDay = 5m;

    /// <summary>
    /// Food and medicine from the home world's own sales ranking worth adding as a
    /// secondary line: real daily volume, worth a retainer slot, and clearly cheaper
    /// elsewhere. Only food and medicine are ever added automatically; everything
    /// else the ranking finds stays a suggestion on the Market tab. Every qualifying
    /// line is returned, best first, so a line that still qualifies is never
    /// mistaken for one that stopped; the merge decides how many are added.
    /// </summary>
    public static IReadOnlyList<ProcurementRule> ProposeFromHomeRanking(
        IReadOnlyList<MarketCandidate> candidates,
        Func<uint, MarketItemFacts?> lookup,
        IReadOnlySet<uint> alreadyConfigured,
        FeeModel fees)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(alreadyConfigured);
        ArgumentNullException.ThrowIfNull(fees);
        var proposals = new List<(ProcurementRule Rule, decimal Score)>();
        foreach (var candidate in candidates)
        {
            if (candidate.ItemId == 0 || alreadyConfigured.Contains(candidate.ItemId) ||
                candidate.UnitsPerDay < MinimumUnitsSoldPerDay ||
                candidate.SalesPerDay < HomeRankingMinimumSalesPerDay ||
                (ulong)candidate.HomePrice * 99 < MinimumStackValue ||
                candidate.NetRoiPercent(fees.MarketTaxPercent, fees.BuyerFeePercent) is not { } roi ||
                roi < HomeRankingMinimumNetRoiPercent)
                continue;
            if (lookup(candidate.ItemId) is not { IsTradable: true, IsFoodOrMedicine: true } facts ||
                facts.CanBeHighQuality != candidate.IsHighQuality)
                continue;
            proposals.Add((new ProcurementRule
            {
                ItemId = candidate.ItemId,
                ItemName = string.IsNullOrWhiteSpace(facts.Name) ? candidate.ItemName : facts.Name,
                TargetStackSize = Math.Clamp(facts.StackSize, 1, 99),
                ListingStackSize = BuyerListingSize(candidate.UnitsPerSale),
                // A floor, not a ceiling: demand decides how many lots it may hold.
                MaximumSaleSlots = 1,
                MinimumWeeklyUnitsSold = 20,
                AllowHighQuality = facts.CanBeHighQuality,
                RequireHighQuality = facts.CanBeHighQuality,
                ListFromBags = true,
                BagReserveQuantity = 0,
                HuntOnTour = true,
                TourPriority = 3,
                Consumable = true,
                // A secondary line, never core stock: nothing found here is pinned.
                PreferredStock = false,
                Confidence = MarketConfidence.Candidate,
                DiscoveredAutomatically = true,
                LastObservedUnitPrice = candidate.HomePrice,
                LastObservedSalesPerDay = candidate.UnitsPerDay,
            }, candidate.HomeGilPerDay * roi));
        }
        return proposals.OrderByDescending(x => x.Score).ThenBy(x => x.Rule.ItemId).Select(x => x.Rule).ToArray();
    }

    /// <summary>
    /// A round listing size near what one buyer typically takes. Listing value is
    /// still lifted to the minimum at listing time.
    /// </summary>
    public static int BuyerListingSize(decimal unitsPerSale)
    {
        if (unitsPerSale <= 0)
            return 0;
        var size = unitsPerSale switch
        {
            <= 5m => decimal.Round(unitsPerSale),
            <= 25m => decimal.Round(unitsPerSale / 5m) * 5m,
            _ => decimal.Round(unitsPerSale / 10m) * 10m,
        };
        return (int)Math.Clamp(size, 1m, 99m);
    }
}
