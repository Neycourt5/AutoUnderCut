using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class BagStockValuationService
{
    public static IReadOnlyList<PortfolioBagStockEstimate> Estimate(
        IReadOnlyCollection<PortfolioBagHolding> holdings,
        IReadOnlyCollection<ProcurementRule> procurementRules,
        uint consumableReserve,
        IReadOnlyCollection<PortfolioBagPrice> prices,
        decimal sellerFeePercent)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        ArgumentNullException.ThrowIfNull(procurementRules);
        ArgumentNullException.ThrowIfNull(prices);

        var rules = procurementRules.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.First());
        var priceByItem = prices.GroupBy(x => (x.ItemId, x.IsHighQuality))
            .ToDictionary(x => x.Key, x => x.Last());
        var estimates = new List<PortfolioBagStockEstimate>();
        foreach (var group in holdings.Where(x => x.ItemId != 0 && x.Quantity > 0)
                     .GroupBy(x => (x.ItemId, x.IsHighQuality)))
        {
            var name = group.First().ItemName;
            rules.TryGetValue(group.Key.ItemId, out var rule);
            if (!ResaleStockPolicy.CanListFromBags(rule, name, group.Key.IsHighQuality))
                continue;

            var total = group.Aggregate<PortfolioBagHolding, ulong>(0, (sum, x) => sum + x.Quantity);
            var reserve = ResaleStockPolicy.BagReserve(rule, name, consumableReserve);
            if (total <= reserve)
                continue;

            // This is future sale stock, including surplus beyond today's free
            // retainer slots. A listing queue describes these same physical items
            // and must never be added as a second holding.
            var quantity = (uint)Math.Min(uint.MaxValue, total - reserve);
            priceByItem.TryGetValue(group.Key, out var price);
            var source = BagValuationSource.Unknown;
            uint unitPrice = 0;
            if (price?.HomeMarketUnitPrice is > 0)
            {
                unitPrice = price.HomeMarketUnitPrice.Value;
                source = BagValuationSource.HomeMarket;
            }
            else if (price?.TrackedUnitCost is > 0)
            {
                unitPrice = price.TrackedUnitCost.Value;
                source = BagValuationSource.PurchaseCost;
            }

            estimates.Add(new(group.Key.ItemId, name, group.Key.IsHighQuality,
                quantity, unitPrice, source, Math.Clamp(sellerFeePercent, 0m, 100m)));
        }

        return estimates.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.IsHighQuality).ToArray();
    }
}
