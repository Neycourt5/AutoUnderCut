namespace SmartUndercutBot.Core.Services;

/// <summary>Reserve a bounded replacement buffer when the listed stock mix is short of core lines.</summary>
public static class AdaptiveShoppingPolicy
{
    public static int PreferredRestockSlots(int totalSaleSlots, int preferredListedSlots,
        int preferredBagSlots, decimal preferredTargetPercent)
    {
        var target = (int)decimal.Ceiling(Math.Max(0, totalSaleSlots) *
            Math.Clamp(preferredTargetPercent, 0m, 100m) / 100m);
        var missing = Math.Max(0, target - Math.Max(0, preferredListedSlots));
        // Full shelves of cheap stock should not stop acquiring useful replacements,
        // but neither should they justify accumulating an entire second portfolio.
        return Math.Max(0, Math.Min(missing, ResaleStockPolicy.ComfortableBagTarget(totalSaleSlots)) -
            Math.Max(0, preferredBagSlots));
    }
}
