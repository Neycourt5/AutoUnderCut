namespace SmartUndercutBot.Core.Services;

/// <summary>
/// The one place transaction costs are modelled. Every ROI figure - the planner's
/// price ceiling, the number shown in the log, the live pre-purchase re-check and
/// the resale floor pinned after a buy - must come from here, or the bot enforces
/// one bar and reports another.
///
/// Both rates are percentages of the gross price. The buyer fee is added on top of
/// the listed price; the market tax is deducted from the sale proceeds.
/// </summary>
public sealed record FeeModel(decimal MarketTaxPercent = 5m, decimal BuyerFeePercent = 5m)
{
    /// <summary>The rates the game charges by default, used when nothing better is known.</summary>
    public static FeeModel Default { get; } = new();

    public bool IsValid => MarketTaxPercent is >= 0 and < 100 && BuyerFeePercent is >= 0 and <= 100;

    private decimal BuyerMultiplier => 1m + BuyerFeePercent / 100m;
    private decimal SellerMultiplier => 1m - MarketTaxPercent / 100m;

    /// <summary>What a stack actually costs to buy, fee included. Rounded up: never understate a cost.</summary>
    public ulong LandedCost(uint unitPrice, uint quantity) =>
        (ulong)Math.Min(ulong.MaxValue, decimal.Ceiling((decimal)unitPrice * quantity * BuyerMultiplier));

    /// <summary>Landed cost of a single unit, rounded up.</summary>
    public uint LandedUnitCost(uint unitPrice) =>
        (uint)Math.Min(uint.MaxValue, decimal.Ceiling(unitPrice * BuyerMultiplier));

    /// <summary>What one unit returns after the sale tax. Rounded down: never overstate proceeds.</summary>
    public uint NetUnitProceeds(uint salePrice) =>
        (uint)Math.Min(uint.MaxValue, decimal.Floor(salePrice * SellerMultiplier));

    public ulong NetProceeds(uint salePrice, uint quantity) => (ulong)NetUnitProceeds(salePrice) * quantity;

    /// <summary>
    /// The canonical ROI: profit over the money actually put at risk, which is the
    /// landed cost including the buyer fee. This is the only definition in the code.
    /// </summary>
    public static decimal NetRoiPercent(ulong netProceeds, ulong landedCost) =>
        landedCost == 0 ? 0m : ((decimal)netProceeds - landedCost) / landedCost * 100m;

    /// <summary>
    /// The most we may pay per unit and still clear both guards. Inverts the ROI and
    /// per-unit profit requirements through the fees, so the price filter alone
    /// enforces them and nothing downstream has to re-derive the arithmetic.
    /// </summary>
    public uint MaximumUnitPrice(uint salePrice, decimal requiredRoiPercent, uint minimumProfitPerUnit)
    {
        if (!IsValid)
            return 0;
        var net = (decimal)NetUnitProceeds(salePrice);
        var roiDivisor = 1m + Math.Max(0m, requiredRoiPercent) / 100m;
        if (roiDivisor <= 0 || BuyerMultiplier <= 0)
            return 0;
        var roiCeiling = decimal.Floor(net / roiDivisor / BuyerMultiplier);
        var profitCeiling = decimal.Floor(Math.Max(0m, net - minimumProfitPerUnit) / BuyerMultiplier);
        return (uint)Math.Clamp(Math.Min(roiCeiling, profitCeiling), 0m, uint.MaxValue);
    }

    /// <summary>
    /// The listing price whose after-tax proceeds cover <paramref name="requiredNet"/>.
    /// Used for the resale floor so the sale tax cannot eat the invested margin.
    /// </summary>
    public uint ListingPriceForNet(uint requiredNet)
    {
        if (SellerMultiplier <= 0)
            return requiredNet;
        return (uint)Math.Min(PricingStrategyService.MaximumListingPrice,
            decimal.Ceiling(requiredNet / SellerMultiplier));
    }
}
