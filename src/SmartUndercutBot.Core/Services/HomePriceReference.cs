using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public sealed record HomePriceSummary(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Listings,
    uint Lowest,
    uint Median,
    uint Reference,
    int Ignored);

/// <summary>
/// The home-world reference price a deal is judged against. A single wildly
/// underpriced listing should not become that reference: one mistake or one
/// undercut war would otherwise make every away-world deal look unprofitable.
/// </summary>
public static class HomePriceReference
{
    /// <summary>A listing this far below the median is treated as an outlier.</summary>
    public const decimal OutlierFractionOfMedian = 0.5m;

    /// <summary>
    /// Drops listings priced below half the median, but only when there are enough
    /// listings for a median to mean anything. With one or two listings there is no
    /// basis to call any of them wrong, so all are kept.
    /// </summary>
    public static IReadOnlyList<ProcurementMarketListing> WithoutOutliers(
        IReadOnlyList<ProcurementMarketListing> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);
        var priced = listings.Where(x => x.PricePerUnit > 0 && x.Quantity > 0).ToArray();
        if (priced.Length < 3)
            return priced;
        var median = MedianPrice(priced);
        if (median == 0)
            return priced;
        var floor = decimal.Floor(median * OutlierFractionOfMedian);
        var kept = priced.Where(x => x.PricePerUnit >= floor).ToArray();
        // Never discard everything: if the whole board sits under the floor the
        // median itself was the outlier.
        return kept.Length == 0 ? priced : kept;
    }

    /// <summary>
    /// The price our stack will realistically meet, given how much cheaper stock is
    /// actually in front of it.
    ///
    /// The cheapest listing is not automatically the market. If popcorn sells 150
    /// units a day and someone has three units up cheap, those three are gone within
    /// minutes and never touch a 99-stack's economics. So walk the board upwards,
    /// accumulating competing units, and take the first price at which the cheap
    /// inventory ahead of us exceeds what the market absorbs in
    /// <paramref name="absorptionDays"/>. Real depth still moves the anchor: a
    /// hundred cheap units in a market selling fifty a day is a genuine problem.
    ///
    /// With no velocity, or no absorption allowance, this degrades exactly to the
    /// cheapest listing - the old, conservative behaviour.
    /// </summary>
    public static uint DepthAdjustedLowest(
        IReadOnlyList<ProcurementMarketListing> listings, decimal salesPerDay, decimal absorptionDays)
    {
        ArgumentNullException.ThrowIfNull(listings);
        // Never discard substantial cheap inventory merely because it is a price outlier.
        var priced = (absorptionDays > 0 ? listings.Where(x => x.PricePerUnit > 0 && x.Quantity > 0)
            : WithoutOutliers(listings)).OrderBy(x => x.PricePerUnit).ToArray();
        if (priced.Length == 0)
            return 0;
        var absorbable = salesPerDay <= 0 || absorptionDays <= 0
            ? 0m
            : decimal.Floor(salesPerDay * absorptionDays);
        if (absorbable <= 0)
            return priced[0].PricePerUnit;

        decimal cumulative = 0;
        foreach (var listing in priced)
        {
            cumulative += listing.Quantity;
            // The first price with more cheap inventory ahead of it than the market
            // eats in the absorption window is the price we must actually beat.
            if (cumulative > absorbable)
                return listing.PricePerUnit;
        }
        // Everything on the board is absorbable. The dearest listing is the best
        // available evidence of where the market sits once the cheap stock clears.
        return priced[^1].PricePerUnit;
    }

    public static HomePriceSummary Summarize(
        uint itemId, string itemName, bool highQuality, IReadOnlyList<ProcurementMarketListing> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);
        var priced = listings
            .Where(x => x.ItemId == itemId && x.IsHighQuality == highQuality && x.PricePerUnit > 0 && x.Quantity > 0)
            .ToArray();
        var kept = WithoutOutliers(priced);
        return new(itemId, itemName, highQuality,
            priced.Length,
            priced.Select(x => x.PricePerUnit).DefaultIfEmpty().Min(),
            MedianPrice(priced),
            kept.Select(x => x.PricePerUnit).DefaultIfEmpty().Min(),
            priced.Length - kept.Count);
    }

    private static uint MedianPrice(IReadOnlyList<ProcurementMarketListing> listings)
    {
        if (listings.Count == 0)
            return 0;
        var sorted = listings.Select(x => x.PricePerUnit).Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (uint)(((ulong)sorted[middle - 1] + sorted[middle]) / 2)
            : sorted[middle];
    }
}
