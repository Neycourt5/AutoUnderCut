namespace SmartUndercutBot.Core.Services;

public static class ProcurementPriceSafety
{
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
