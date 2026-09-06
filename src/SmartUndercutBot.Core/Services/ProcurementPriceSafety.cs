namespace SmartUndercutBot.Core.Services;

public static class ProcurementPriceSafety
{
    public static uint MinimumResalePrice(uint landedUnitCost, decimal minimumRoiPercent, uint minimumProfitPerUnit)
    {
        var requiredNet = Math.Max(
            landedUnitCost * (1m + Math.Clamp(minimumRoiPercent, 0m, 1_000m) / 100m),
            (decimal)landedUnitCost + minimumProfitPerUnit);
        return (uint)Math.Min(PricingStrategyService.MaximumListingPrice, decimal.Ceiling(decimal.Ceiling(requiredNet) / 0.95m));
    }
}
