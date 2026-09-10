using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// How much of a market we already hold, measured in days of that market's demand,
/// and whether another stack is welcome.
///
/// This replaces "have I reached eight slots?" with "how many days of this market's
/// sales am I already sitting on?". A line selling a hundred units a day can absorb
/// several stacks and millions of gil; a line selling three a day cannot absorb one,
/// however good the margin looks.
/// </summary>
public static class InventoryCoveragePolicy
{
    /// <summary>Days of demand represented by <paramref name="ownedUnits"/>.</summary>
    public static decimal CoverageDays(ulong ownedUnits, decimal salesPerDay) =>
        salesPerDay <= 0
            ? (ownedUnits == 0 ? 0m : PortfolioPolicy.MaximumDaysToSell)
            : ownedUnits / salesPerDay;

    /// <summary>Units representing the target holding for this class of stock.</summary>
    public static ulong TargetUnits(decimal salesPerDay, decimal coverageDays)
    {
        if (salesPerDay <= 0 || coverageDays <= 0)
            return 0;
        var units = decimal.Ceiling(salesPerDay * coverageDays);
        return (ulong)Math.Clamp(units, 0m, (decimal)ulong.MaxValue);
    }

    /// <summary>
    /// Whether one more stack of this size may be bought.
    ///
    /// Two conditions, and both matter. Holdings must currently be below target, so
    /// a full position stops buying outright rather than topping up forever. And the
    /// purchase may not carry holdings past target plus the overshoot allowance, so a
    /// single stack may complete a position but a second cannot pile on top of it.
    ///
    /// When velocity is unknown the answer is no: without demand there is no basis
    /// for sizing a position, and the caller falls back to its own limits.
    /// </summary>
    public static bool CanAdd(
        ulong ownedUnits, uint quantity, decimal salesPerDay, decimal coverageDays, decimal overshootDays)
    {
        if (quantity == 0 || salesPerDay <= 0 || coverageDays <= 0)
            return false;
        var target = TargetUnits(salesPerDay, coverageDays);
        if (target == 0 || ownedUnits >= target)
            return false;
        var ceiling = TargetUnits(salesPerDay, coverageDays + Math.Max(0m, overshootDays));
        return ownedUnits + quantity <= Math.Max(target, ceiling);
    }

    /// <summary>
    /// Sale slots this market's demand justifies, used to lift the fixed per-item
    /// slot cap for markets that genuinely support more capital. Bounded by the
    /// emergency limit so a velocity spike cannot concentrate the whole portfolio.
    /// </summary>
    public static int DemandJustifiedSlots(
        decimal salesPerDay, decimal coverageDays, int stackSize, int emergencyMaximum)
    {
        if (salesPerDay <= 0 || coverageDays <= 0 || stackSize <= 0)
            return 0;
        var units = TargetUnits(salesPerDay, coverageDays);
        var slots = (int)Math.Min(int.MaxValue, (units + (ulong)stackSize - 1) / (ulong)stackSize);
        return Math.Clamp(slots, 0, Math.Max(0, emergencyMaximum));
    }
}
