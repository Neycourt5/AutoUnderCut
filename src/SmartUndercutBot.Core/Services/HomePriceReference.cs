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
