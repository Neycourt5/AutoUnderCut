namespace SmartUndercutBot.Core.Services;

public static class MarketPriceSafety
{
    // The curated consumables handled by automatic bag listing normally trade
    // for thousands, not millions. This ceiling also catches old placeholder
    // artifacts without affecting the plugin's general-purpose repricer.
    public const uint MaximumCuratedUnitPrice = 1_000_000;

    public static bool IsSafetySeedRepresentation(uint unitPrice, uint quantity)
    {
        if (unitPrice == PricingStrategyService.MaximumListingPrice)
            return true;
        if (unitPrice == 0 || quantity == 0)
            return false;

        // FFXIV sometimes converts a 999,999,999 unit-price seed into a unit
        // price whose stack total is approximately 999,999,999. For x99 this
        // appears as 10,101,010 gil per unit.
        var total = (ulong)unitPrice * quantity;
        return total >= PricingStrategyService.MaximumListingPrice - 1_000UL &&
               total <= PricingStrategyService.MaximumListingPrice + 1_000UL;
    }

    public static bool IsSafeCuratedUnitPrice(uint unitPrice, uint quantity = 99) =>
        unitPrice is > 0 and <= MaximumCuratedUnitPrice &&
        !IsSafetySeedRepresentation(unitPrice, quantity);
}
