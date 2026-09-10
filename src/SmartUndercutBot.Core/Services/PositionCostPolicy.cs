using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// What the inventory currently held actually cost, and the resale floor that
/// protects it.
///
/// The old rule took MAX(oldBasis, newBasis) and never let go, so one expensive
/// acquisition pinned an unsellable floor on an otherwise healthy line forever.
/// The basis here is a weighted average over the units on hand: buying more of
/// something cheaply pulls the basis down, and selling a position out and
/// restocking rebases it entirely. Capital is still protected, because the floor
/// covers the average cost of what is actually sitting in the bags.
/// </summary>
public static class PositionCostPolicy
{
    /// <summary>
    /// Blend a confirmed purchase into the tracked position.
    ///
    /// <paramref name="holdingsKnown"/> is the honest part. When the caller cannot
    /// see how much of the item is really held, the old units backing the basis are
    /// unknown, and averaging against a guess could lower the floor below what the
    /// existing stock cost. In that case the protective MAX is kept. This is the
    /// documented approximation: exact per-lot accounting is not available, so the
    /// basis is a weighted average when holdings are observable and a high-water
    /// mark when they are not.
    /// </summary>
    public static void RecordPurchase(PricingRule rule, ulong heldUnits, uint quantity,
        ulong landedCost, decimal roi, uint minimumProfit, bool holdingsKnown, FeeModel? fees = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (quantity == 0)
            return;
        var unit = (uint)Math.Min(uint.MaxValue, decimal.Ceiling((decimal)landedCost / quantity));
        // Only units the existing basis actually accounts for may dilute it. A
        // position larger than the recorded units is treated as recorded units, so
        // stock of unknown cost cannot be averaged in at a price it never paid.
        var blendUnits = Math.Min(heldUnits, rule.CostBasisUnits);
        rule.CostBasis = !holdingsKnown || rule.CostBasis == 0
            ? Math.Max(rule.CostBasis, unit)
            : (uint)Math.Min(uint.MaxValue, decimal.Ceiling(
                ((decimal)rule.CostBasis * blendUnits + landedCost) / (blendUnits + quantity)));
        rule.CostBasisUnits = (uint)Math.Min(uint.MaxValue, (ulong)blendUnits + quantity);
        rule.AcquisitionFloor = ProcurementPriceSafety.MinimumResalePrice(
            rule.CostBasis, roi, minimumProfit, fees);
    }

    /// <summary>
    /// Retire cost information for units that have left the position. Once the
    /// tracked units reach zero the basis is cleared with them, so the next
    /// purchase rebases the item instead of inheriting an old, higher price.
    /// </summary>
    public static void RecordSale(PricingRule rule, uint quantity)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (quantity == 0 || rule.CostBasisUnits == 0)
            return;
        rule.CostBasisUnits = quantity >= rule.CostBasisUnits ? 0 : rule.CostBasisUnits - quantity;
        if (rule.CostBasisUnits != 0)
            return;
        rule.CostBasis = 0;
        rule.AcquisitionFloor = 0;
    }
}
