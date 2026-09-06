using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public interface IProcurementPlannerService
{
    ProcurementPlan BuildPlan(ProcurementPlanRequest request);
    ProcurementPlan BuildLiveMarketPlan(LiveMarketPlanRequest request);
}

public sealed class ProcurementPlannerService : IProcurementPlannerService
{
    public ProcurementPlan BuildPlan(ProcurementPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0 ||
            request.MarketTaxPercent is < 0 or > 100 || request.BuyerFeePercent is < 0 or > 100 ||
            request.MinimumRoiPercent is < 0 or > 1_000)
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

            foreach (var quality in EligibleQualities(rule))
            {
                var sales = market.RecentSales
                    .Where(x => x.IsHighQuality == quality)
                    .Where(x => x.PricePerUnit > 0 && x.Quantity > 0 &&
                                x.SoldAt >= DateTimeOffset.UtcNow.AddDays(-7) && x.SoldAt <= DateTimeOffset.UtcNow)
                    .ToArray();
                if (sales.Sum(x => (long)x.Quantity) < rule.MinimumWeeklyUnitsSold)
                    continue;

                var targetSalePrice = Median(sales.Select(x => x.PricePerUnit));
                if (targetSalePrice == 0)
                    continue;

                var netUnitProceeds = decimal.Floor(targetSalePrice * (1m - request.MarketTaxPercent / 100m));
                var buyerFeeMultiplier = 1m + request.BuyerFeePercent / 100m;
                var roiDivisor = 1m + request.MinimumRoiPercent / 100m;
                var roiCeiling = roiDivisor <= 0 || buyerFeeMultiplier <= 0
                    ? 0
                    : decimal.Floor(netUnitProceeds / roiDivisor / buyerFeeMultiplier);
                var profitCeiling = buyerFeeMultiplier <= 0
                    ? 0
                    : decimal.Floor(Math.Max(0, netUnitProceeds - request.MinimumProfitPerUnit) / buyerFeeMultiplier);
                var ceiling = (uint)Math.Min(uint.MaxValue, Math.Min(roiCeiling, profitCeiling));
                if (rule.MaximumUnitPrice > 0)
                    ceiling = Math.Min(ceiling, rule.MaximumUnitPrice);
                if (ceiling == 0)
                    continue;

                foreach (var listing in market.Listings)
                {
                    if (listing.ItemId != market.ItemId || listing.PricePerUnit == 0 || listing.PricePerUnit > ceiling ||
                        string.IsNullOrWhiteSpace(listing.WorldName) ||
                        listing.Quantity == 0 || listing.IsHighQuality != quality ||
                        listing.Quantity > Math.Max(1, rule.TargetStackSize))
                        continue;

                    var totalCost = PurchaseCost(listing.PricePerUnit, listing.Quantity, request.BuyerFeePercent);
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
            var cost = PurchaseCost(candidate.PricePerUnit, candidate.Quantity, request.BuyerFeePercent);
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

    public ProcurementPlan BuildLiveMarketPlan(LiveMarketPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0 ||
            string.IsNullOrWhiteSpace(request.HomeWorld) || request.MarketTaxPercent is < 0 or > 100 ||
            request.BuyerFeePercent is < 0 or > 100 || request.MinimumRoiPercent is < 0 or > 1_000)
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

            foreach (var quality in EligibleQualities(rule))
            {
                bool QualityMatches(ProcurementMarketListing listing) => listing.IsHighQuality == quality;

                // The fallback tour deliberately ignores Universalis. Its resale anchor is
                // the cheapest live, in-game listing on the home world, excluding the
                // player's own retainers so a self-listing cannot manufacture a deal.
                var targetSalePrice = market.Listings
                    .Where(x => x.ItemId == market.ItemId && x.Quantity > 0)
                    .Where(x => string.Equals(x.WorldName, request.HomeWorld, StringComparison.OrdinalIgnoreCase))
                    .Where(QualityMatches)
                    .Where(x => x.PricePerUnit > 0 && !request.OwnedRetainerIds.Contains(x.RetainerId))
                    .Select(x => x.PricePerUnit)
                    .DefaultIfEmpty()
                    .Min();
                if (targetSalePrice == 0)
                    continue;

                var netUnitProceeds = decimal.Floor(targetSalePrice * (1m - request.MarketTaxPercent / 100m));
                var buyerFeeMultiplier = 1m + request.BuyerFeePercent / 100m;
                var roiDivisor = 1m + request.MinimumRoiPercent / 100m;
                var roiCeiling = roiDivisor <= 0 || buyerFeeMultiplier <= 0
                    ? 0
                    : decimal.Floor(netUnitProceeds / roiDivisor / buyerFeeMultiplier);
                var profitCeiling = buyerFeeMultiplier <= 0
                    ? 0
                    : decimal.Floor(Math.Max(0, netUnitProceeds - request.MinimumProfitPerUnit) / buyerFeeMultiplier);
                var ceiling = (uint)Math.Min(uint.MaxValue, Math.Min(roiCeiling, profitCeiling));
                if (rule.MaximumUnitPrice > 0)
                    ceiling = Math.Min(ceiling, rule.MaximumUnitPrice);
                if (ceiling == 0)
                    continue;

                foreach (var listing in market.Listings)
                {
                    if (listing.ItemId != market.ItemId || string.IsNullOrWhiteSpace(listing.WorldName) ||
                        !QualityMatches(listing) || request.OwnedRetainerIds.Contains(listing.RetainerId) ||
                        listing.PricePerUnit == 0 || listing.PricePerUnit > ceiling || listing.Quantity == 0 ||
                        listing.Quantity > Math.Max(1, rule.TargetStackSize))
                        continue;

                    var totalCost = PurchaseCost(listing.PricePerUnit, listing.Quantity, request.BuyerFeePercent);
                    var totalNet = (ulong)(uint)netUnitProceeds * listing.Quantity;
                    if (totalCost > uint.MaxValue || totalNet <= totalCost)
                        continue;
                    var expectedProfit = Math.Min((ulong)uint.MaxValue, totalNet - totalCost);
                    candidates.Add(new(
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
            var usedForItem = itemSlots.GetValueOrDefault(candidate.ItemId);
            var cost = PurchaseCost(candidate.PricePerUnit, candidate.Quantity, request.BuyerFeePercent);
            if (orders.Count >= slotLimit || usedForItem >= rules[candidate.ItemId].MaximumSaleSlots ||
                spent + cost > request.GilBudget)
                continue;
            orders.Add(candidate);
            itemSlots[candidate.ItemId] = usedForItem + 1;
            spent += cost;
            profit += candidate.ExpectedProfit;
        }

        return new(
            DateTimeOffset.UtcNow,
            orders,
            (uint)Math.Min(spent, uint.MaxValue),
            (uint)Math.Min(profit, uint.MaxValue),
            orders.Count);
    }

    private static IEnumerable<bool> EligibleQualities(ProcurementRule rule)
    {
        if (!rule.RequireHighQuality)
            yield return false;
        if (rule.AllowHighQuality || rule.RequireHighQuality)
            yield return true;
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

    private static ulong PurchaseCost(uint unitPrice, uint quantity, decimal buyerFeePercent)
    {
        var subtotal = (decimal)unitPrice * quantity;
        return (ulong)Math.Min(ulong.MaxValue, decimal.Ceiling(subtotal * (1m + buyerFeePercent / 100m)));
    }
}
