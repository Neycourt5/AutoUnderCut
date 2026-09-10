using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class SalesVelocityPolicy
{
    // Universalis velocity counts units sold per day, separately for HQ and NQ.
    // It ranks opportunities; it never replaces actual sales/price/budget guards.
    public static decimal DailyUnits(ProcurementMarketItem? market, bool highQuality, DateTimeOffset? now = null)
    {
        if (market is null) return 0;
        var reported = highQuality ? market.HqSalesPerDay : market.NqSalesPerDay;
        // A reported zero is "the source has nothing to say", not "this market is
        // dead": Universalis returns 0 for a quality it has no window for. Taking it
        // literally used to suppress the seven-day fallback, so an item with real
        // observed sales scored as if it never moved - which then denied it the
        // volume-based margin bar and made coverage refuse to size a position.
        if (reported is > 0 and <= 1_000_000_000m) return reported.Value;
        var at = now ?? DateTimeOffset.UtcNow;
        // Missing statistics stay conservative; a burst of sales in a tiny sample
        // must not manufacture a huge daily rate.
        return market.RecentSales.Where(s => s.IsHighQuality == highQuality && s.PricePerUnit > 0 &&
            s.SoldAt >= at.AddDays(-7) && s.SoldAt <= at).Sum(s => (decimal)s.Quantity) / 7m;
    }
}
