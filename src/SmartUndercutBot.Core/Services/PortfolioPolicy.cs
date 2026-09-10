using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// The economic objective: hold a high-quality trading portfolio first and only
/// then spend what is left on secondary opportunities. Filling a sale slot is the
/// last consideration, not the first - an empty slot beats a slot of junk.
/// </summary>
public static class PortfolioPolicy
{
    /// <summary>
    /// A tiny listing must not produce an absurd profit-per-day score, and an item
    /// nothing is buying must not look like it clears instantly.
    /// </summary>
    public const decimal MinimumDaysToSell = 0.25m;
    public const decimal MaximumDaysToSell = 30m;

    /// <summary>
    /// Liquidity floors that promote an unpinned item to <see cref="PortfolioTier.Secondary"/>.
    /// These describe "real trading stock" rather than a lucky margin, and are
    /// deliberately constants: exposing them as settings would add knobs without
    /// changing behaviour that the value and cap percentages do not already cover.
    /// A curated stack carries hundreds of thousands of gil in one sale slot, so a
    /// twenty-unit dye stack worth forty thousand is not the same class of stock
    /// however fast it moves - that is what the value floor is for.
    /// </summary>
    public const decimal SecondaryMinimumSalesPerDay = 10m;
    public const uint SecondaryMinimumValuePerSlot = 150_000;

    /// <summary>
    /// The margin a purchase has to clear. Stock that turns over daily earns its
    /// return from velocity rather than from the size of each flip: a stack of
    /// something selling ten-plus units a day is gone in hours and the gil is back
    /// out working, so demanding a fat margin on it mostly leaves the gil idle.
    /// Slower stock keeps the full bar, because there the margin is the whole
    /// return. A negative fast-mover value means the caller did not set one.
    /// </summary>
    public static decimal RequiredRoiPercent(decimal minimumRoi, decimal fastMoverRoi, decimal salesPerDay) =>
        fastMoverRoi >= 0 && salesPerDay >= SecondaryMinimumSalesPerDay
            ? Math.Min(minimumRoi, fastMoverRoi)
            : minimumRoi;

    /// <summary>Ranking order. Core stock is considered before anything else.</summary>
    public static int Rank(PortfolioTier tier) => tier switch
    {
        PortfolioTier.Core => 0,
        PortfolioTier.Secondary => 1,
        _ => 2,
    };

    /// <summary>
    /// How long a listing of this size takes to clear at the observed home-world
    /// rate. The rate is the whole market's, not our share of it, so this is an
    /// optimistic ranking signal rather than a promise; the clamps bound both ends.
    /// </summary>
    public static decimal DaysToSell(uint quantity, decimal salesPerDay) =>
        salesPerDay <= 0 || quantity == 0
            ? MaximumDaysToSell
            : Math.Clamp(quantity / salesPerDay, MinimumDaysToSell, MaximumDaysToSell);

    /// <summary>Expected profit per day of sale-slot occupancy.</summary>
    public static decimal ProfitVelocity(uint expectedProfit, decimal daysToSell) =>
        expectedProfit / Math.Max(MinimumDaysToSell, daysToSell);

    /// <summary>
    /// Tier for something we are considering buying. Pinned stock is always core.
    /// Everything else must earn Secondary with actual market characteristics:
    /// meaningful value per occupied slot, meaningful profit per occupied slot and
    /// real sales velocity. ROI is not part of this - a huge percentage on a
    /// worthless item is exactly what this tiering exists to demote.
    /// </summary>
    public static PortfolioTier ClassifyCandidate(
        bool preferred, decimal salesPerDay, ulong valuePerSlot, ulong profitPerSlot, PortfolioGates gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        if (preferred)
            return PortfolioTier.Core;
        return salesPerDay >= SecondaryMinimumSalesPerDay &&
               valuePerSlot >= SecondaryMinimumValuePerSlot &&
               profitPerSlot >= gates.MinimumProfitPerSaleSlot
            ? PortfolioTier.Secondary
            : PortfolioTier.Opportunistic;
    }

    /// <summary>
    /// Tier for stock already held or listed. Profit is sunk on stock we own, so
    /// only value and liquidity decide; an item with no known market data counts as
    /// opportunistic, which is what makes a listed pile of dye consume the cap.
    /// </summary>
    public static PortfolioTier ClassifyHolding(bool preferred, decimal salesPerDay, ulong valuePerSlot) =>
        preferred ? PortfolioTier.Core
            : salesPerDay >= SecondaryMinimumSalesPerDay && valuePerSlot >= SecondaryMinimumValuePerSlot
                ? PortfolioTier.Secondary
                : PortfolioTier.Opportunistic;

    /// <summary>
    /// Turn the tier percentages into slot counts. The denominator is the whole
    /// trading portfolio, not one shopping run, so already-listed stock counts and
    /// the next purchases rebalance toward what is short.
    /// </summary>
    public static PortfolioAllocationSummary Summarize(
        IReadOnlyDictionary<PortfolioTier, int> ownedSlots, int addedSlots, int capacitySlots, PortfolioGates gates)
    {
        ArgumentNullException.ThrowIfNull(ownedSlots);
        ArgumentNullException.ThrowIfNull(gates);
        var core = ownedSlots.GetValueOrDefault(PortfolioTier.Core);
        var secondary = ownedSlots.GetValueOrDefault(PortfolioTier.Secondary);
        var opportunistic = ownedSlots.GetValueOrDefault(PortfolioTier.Opportunistic);
        var capacity = Math.Max(capacitySlots, core + secondary + opportunistic + Math.Max(0, addedSlots));
        return new(
            core,
            (int)decimal.Ceiling(capacity * Math.Clamp(gates.PreferredTargetPercent, 0m, 100m) / 100m),
            secondary,
            opportunistic,
            (int)decimal.Floor(capacity * Math.Clamp(gates.OpportunisticMaximumPercent, 0m, 100m) / 100m),
            capacity);
    }
}
