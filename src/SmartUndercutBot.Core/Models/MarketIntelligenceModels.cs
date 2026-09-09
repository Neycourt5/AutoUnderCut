namespace SmartUndercutBot.Core.Models;

/// <summary>
/// A daily-ish market aggregate for one item. This is intelligence, not
/// authorization: it decides what is worth looking at and where, and can never
/// approve a purchase on its own. Purchases require a fresh live board reading.
/// </summary>
public sealed record MarketStatistic(
    uint ItemId,
    string ItemName,
    uint MedianNqPrice,
    uint MedianHqPrice,
    decimal NqUnitsSoldPerDay,
    decimal HqUnitsSoldPerDay,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Best available price signal, preferring high quality.</summary>
    public uint BestPrice => MedianHqPrice > 0 ? MedianHqPrice : MedianNqPrice;
    public decimal BestUnitsSoldPerDay => Math.Max(NqUnitsSoldPerDay, HqUnitsSoldPerDay);
}

/// <summary>What local game data says about an item, used to validate a candidate.</summary>
public sealed record MarketItemFacts(
    uint ItemId,
    string Name,
    bool IsTradable,
    bool CanBeHighQuality,
    bool IsFoodOrMedicine,
    int StackSize);

/// <summary>
/// A per-item statistics source. Universalis and Saddlebag Exchange both provide
/// one; procurement never depends on a particular vendor.
/// </summary>
public interface IMarketStatisticsProvider
{
    string Name { get; }
    bool IsEnabled { get; }

    /// <summary>
    /// Statistics for the given items, or for the whole market when the list is
    /// empty and the provider supports it. Returns an empty list rather than
    /// throwing when the source is unavailable.
    /// </summary>
    Task<IReadOnlyList<MarketStatistic>> GetStatisticsAsync(
        IReadOnlyList<uint> itemIds, string scope, CancellationToken cancellationToken);
}

/// <summary>Proposes stock worth trading, from statistics plus local item facts.</summary>
public interface IMarketDiscoveryProvider
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<IReadOnlyList<ProcurementRule>> DiscoverAsync(CancellationToken cancellationToken);
}
