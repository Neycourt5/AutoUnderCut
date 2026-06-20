using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public interface IProcurementPlannerService
{
    ProcurementPlan BuildPlan(ProcurementPlanRequest request);
}

public sealed class ProcurementPlannerService : IProcurementPlannerService
{
    public ProcurementPlan BuildPlan(ProcurementPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0)
            return ProcurementPlan.Empty;

        var rules = request.Rules
            .Where(x => x.Enabled && x.ItemId != 0)
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.First());
        var candidates = new List<ProcurementOrder>();

        foreach (var market in request.Markets)
        {
            if (!rules.TryGetValue(market.ItemId, out var rule))
                continue;

            var sales = market.RecentSales
                .Where(x => rule.AllowHighQuality || !x.IsHighQuality)
                .Where(x => x.PricePerUnit > 0 && x.SoldAt >= DateTimeOffset.UtcNow.AddDays(-7))
                .ToArray();
            if (sales.Sum(x => (long)x.Quantity) < rule.MinimumWeeklyUnitsSold)
                continue;

            var targetSalePrice = Median(sales.Select(x => x.PricePerUnit));
            if (targetSalePrice == 0)
                continue;

            var netUnitProceeds = decimal.Floor(targetSalePrice * (1m - request.MarketTaxPercent / 100m));
            var roiDivisor = 1m + request.MinimumRoiPercent / 100m;
            var roiCeiling = roiDivisor <= 0 ? 0 : decimal.Floor(netUnitProceeds / roiDivisor);
            var profitCeiling = Math.Max(0, netUnitProceeds - request.MinimumProfitPerUnit);
            var ceiling = (uint)Math.Min(uint.MaxValue, Math.Min(roiCeiling, profitCeiling));
            if (rule.MaximumUnitPrice > 0)
                ceiling = Math.Min(ceiling, rule.MaximumUnitPrice);
            if (ceiling == 0)
                continue;

            foreach (var listing in market.Listings)
            {
                if (listing.ItemId != market.ItemId || listing.PricePerUnit == 0 || listing.PricePerUnit > ceiling ||
                    listing.Quantity == 0 || listing.IsHighQuality && !rule.AllowHighQuality ||
                    listing.Quantity > Math.Max(1, rule.TargetStackSize))
                    continue;

                var totalCost = (ulong)listing.PricePerUnit * listing.Quantity;
                var totalNet = (ulong)(uint)netUnitProceeds * listing.Quantity;
                if (totalCost > uint.MaxValue || totalNet <= totalCost)
                    continue;
                var expectedProfit = totalNet - totalCost;
                if (expectedProfit > uint.MaxValue)
                    expectedProfit = uint.MaxValue;

                candidates.Add(new ProcurementOrder(
                    market.ItemId,
                    string.IsNullOrWhiteSpace(rule.ItemName) ? market.ItemName : rule.ItemName,
                    listing.ListingId,
                    listing.RetainerId,
                    listing.WorldName,
                    listing.WorldId,
                    listing.PricePerUnit,
                    listing.Quantity,
                    listing.IsHighQuality,
                    targetSalePrice,
                    ceiling,
                    (uint)expectedProfit,
                    1));
            }
        }

        var slotLimit = Math.Min(request.FreeSaleSlots, request.FreeInventorySlots);
        var orders = new List<ProcurementOrder>(slotLimit);
        var itemSlots = new Dictionary<uint, int>();
        ulong spent = 0;
        ulong profit = 0;
        foreach (var candidate in candidates
                     .OrderByDescending(x => x.ExpectedProfit)
                     .ThenBy(x => x.PricePerUnit))
        {
            var rule = rules[candidate.ItemId];
            var usedForItem = itemSlots.GetValueOrDefault(candidate.ItemId);
            var cost = (ulong)candidate.PricePerUnit * candidate.Quantity;
            if (orders.Count >= slotLimit || usedForItem >= rule.MaximumSaleSlots || spent + cost > request.GilBudget)
                continue;

            orders.Add(candidate);
            itemSlots[candidate.ItemId] = usedForItem + candidate.SaleSlots;
            spent += cost;
            profit += candidate.ExpectedProfit;
        }

        return new ProcurementPlan(
            DateTimeOffset.UtcNow,
            orders,
            (uint)Math.Min(spent, uint.MaxValue),
            (uint)Math.Min(profit, uint.MaxValue),
            orders.Sum(x => x.SaleSlots));
    }

    private static uint Median(IEnumerable<uint> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (uint)(((ulong)sorted[middle - 1] + sorted[middle]) / 2)
            : sorted[middle];
    }
}
