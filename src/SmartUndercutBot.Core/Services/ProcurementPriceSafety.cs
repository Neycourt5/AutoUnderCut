namespace SmartUndercutBot.Core.Services;

public static class ProcurementPriceSafety
{
    /// <summary>
    /// Preserve the invested cost plus at least one gil per unit after sale tax.
    /// An unreachable floor remains above the game's price limit, so clamping
    /// cannot turn an impossible profitable sale into an approved loss.
    /// </summary>
    public static uint MinimumProfitableResalePrice(
        uint landedUnitCost, FeeModel? fees = null, decimal minimumMarginPercent = 0m)
    {
        var model = fees ?? FeeModel.Default;
        if (!model.IsValid)
            return uint.MaxValue;
        var requiredNet = Math.Max((decimal)landedUnitCost + 1m,
            landedUnitCost * (1m + Math.Clamp(minimumMarginPercent,
                0m, PricingStrategyService.MaximumListingPrice * 100m) / 100m));
        if (requiredNet > model.NetUnitProceeds(PricingStrategyService.MaximumListingPrice))
            return uint.MaxValue;
        return model.ListingPriceForNet((uint)decimal.Ceiling(requiredNet));
    }

    /// <summary>
    /// The lowest price a stack may be listed at and still return its invested
    /// capital plus the margin it was bought against. The required figure is a net
    /// one, so it is grossed back up through the sale tax by the same fee model the
    /// purchase was approved under - listing at the net figure would hand the tax
    /// straight out of the margin.
    /// </summary>
    public static uint MinimumResalePrice(
        uint landedUnitCost, decimal minimumRoiPercent, uint minimumProfitPerUnit, FeeModel? fees = null)
    {
        var model = fees ?? FeeModel.Default;
        var requiredNet = Math.Max(
            landedUnitCost * (1m + Math.Clamp(minimumRoiPercent, 0m, 1_000m) / 100m),
            (decimal)landedUnitCost + minimumProfitPerUnit);
        var net = (uint)Math.Min(PricingStrategyService.MaximumListingPrice, decimal.Ceiling(requiredNet));
        return model.ListingPriceForNet(net);
    }
}
