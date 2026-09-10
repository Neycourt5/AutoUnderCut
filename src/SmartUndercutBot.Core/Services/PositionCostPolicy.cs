using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class PositionCostPolicy
{
    public static void RecordPurchase(PricingRule rule, ulong heldUnits, uint quantity,
        ulong landedCost, decimal roi, uint minimumProfit, bool holdingsKnown)
    {
        if (quantity == 0) return;
        var unit = (uint)Math.Min(uint.MaxValue, decimal.Ceiling((decimal)landedCost / quantity));
        // Unknown holdings keep the more protective basis; a known position blends
        // its remaining cost with the new acquisition, including actual buyer tax.
        rule.CostBasis = !holdingsKnown ? Math.Max(rule.CostBasis, unit)
            : (uint)Math.Min(uint.MaxValue, decimal.Ceiling(
                ((decimal)rule.CostBasis * heldUnits + landedCost) / (heldUnits + quantity)));
        rule.CostBasisUnits = (uint)Math.Min(uint.MaxValue, heldUnits + quantity);
        rule.AcquisitionFloor = ProcurementPriceSafety.MinimumResalePrice(rule.CostBasis, roi, minimumProfit);
    }
}
