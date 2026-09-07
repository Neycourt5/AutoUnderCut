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
            request.MinimumRoiPercent is < 0 or > 1_000 ||
            request.MaximumWeeklySalesSharePercent is <= 0 or > 100)
            return ProcurementPlan.Empty;

        var rules = request.Rules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.First());
        var candidates = new List<ProcurementOrder>();
        var remainingUnits = new Dictionary<(uint, bool), ulong>();

        foreach (var market in request.Markets)
        {
            if (!rules.TryGetValue(market.ItemId, out var rule))
                continue;

            foreach (var quality in EligibleQualities(rule, request.HighQualityOnly))
            {
                var sales = market.RecentSales
                    .Where(x => x.IsHighQuality == quality)
                    .Where(x => x.PricePerUnit > 0 && x.Quantity > 0 &&
                                x.SoldAt >= DateTimeOffset.UtcNow.AddDays(-7) && x.SoldAt <= DateTimeOffset.UtcNow)
                    .ToArray();
                if (sales.Sum(x => (long)x.Quantity) < rule.MinimumWeeklyUnitsSold)
                    continue;
                // Cap how much of an item may be held at once, measured against what
                // the market actually absorbs in a week. One target stack is always
                // permitted: the stack size is the declared trade unit, and without
                // this floor a 20-units-per-week minimum and a 25% share can never
                // admit a single 99-stack, so nothing would ever be bought.
                var observedLimit = Math.Max(
                    (ulong)Math.Max(1, rule.TargetStackSize),
                    (ulong)decimal.Floor(sales.Sum(x => (decimal)x.Quantity) *
                        request.MaximumWeeklySalesSharePercent / 100m));
                var ownedUnits = (ulong)(request.OwnedStock ?? []).Where(x =>
                    x.ItemId == market.ItemId && x.IsHighQuality == quality).Sum(x => (long)x.Quantity);
                remainingUnits[(market.ItemId, quality)] = observedLimit > ownedUnits ? observedLimit - ownedUnits : 0;

                var targetSalePrice = Median(sales.Select(x => x.PricePerUnit));
                if (!string.IsNullOrWhiteSpace(request.HomeWorld))
                {
                    // One badly underpriced listing must not become the resale anchor,
                    // or every away-world deal is judged against a mistake.
                    var homeListings = (request.ResaleListings ?? market.Listings)
                        .Where(x => x.ItemId == market.ItemId && x.IsHighQuality == quality &&
                                    x.Quantity > 0 && x.PricePerUnit > 0 &&
                                    string.Equals(x.WorldName, request.HomeWorld, StringComparison.OrdinalIgnoreCase) &&
                                    request.OwnedRetainerIds?.Contains(x.RetainerId) != true)
                        .ToArray();
                    var homeLowest = HomePriceReference.WithoutOutliers(homeListings)
                        .Select(x => x.PricePerUnit).DefaultIfEmpty().Min();
                    // Home-world sales supply the median. Do not buy using a regional
                    // resale estimate or a higher price than local competition supports.
                    targetSalePrice = Math.Min(targetSalePrice, homeLowest > 1 ? homeLowest - 1 : 0);
                }
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
                        request.OwnedRetainerIds?.Contains(listing.RetainerId) == true ||
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

        return Allocate(candidates, rules, request.GilBudget,
            Math.Min(request.FreeSaleSlots, request.FreeInventorySlots), request.BuyerFeePercent,
            request.OwnedStock ?? [], remainingUnits);
    }

    public ProcurementPlan BuildLiveMarketPlan(LiveMarketPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0 ||
            string.IsNullOrWhiteSpace(request.HomeWorld) || request.MarketTaxPercent is < 0 or > 100 ||
            request.BuyerFeePercent is < 0 or > 100 || request.MinimumRoiPercent is < 0 or > 1_000)
            return ProcurementPlan.Empty;

        var rules = request.Rules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.First());
        var candidates = new List<ProcurementOrder>();

        foreach (var market in request.Markets)
        {
            if (!rules.TryGetValue(market.ItemId, out var rule))
                continue;

            foreach (var quality in EligibleQualities(rule, request.HighQualityOnly))
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

        return Allocate(candidates, rules, request.GilBudget,
            Math.Min(request.FreeSaleSlots, request.FreeInventorySlots), request.BuyerFeePercent,
            request.OwnedStock ?? [], null);
    }

    private static ProcurementPlan Allocate(List<ProcurementOrder> candidates,
        IReadOnlyDictionary<uint, ProcurementRule> rules, uint budget, int slotLimit, decimal buyerFee,
        IReadOnlyList<StockExposure> owned, IReadOnlyDictionary<(uint, bool), ulong>? quantityLimits)
    {
        // Absolute profit works well when slots are scarce; ROI can buy more
        // profitable combinations with a small wallet. Compare complete feasible
        // plans instead of letting one expensive stack consume the whole budget.
        ulong Cost(ProcurementOrder x) => PurchaseCost(x.PricePerUnit, x.Quantity, buyerFee);
        // Lower tour priority means higher-volume stock: raid food and potions before
        // dyes before materia. Keeping retainers stocked is the goal, so a fast mover
        // is worth more than a slightly richer margin on something that sits.
        int Priority(ProcurementOrder x) => rules.TryGetValue(x.ItemId, out var rule) ? rule.TourPriority : int.MaxValue;
        var strategies = new[]
        {
            candidates.OrderByDescending(x => (double)x.ExpectedProfit).ThenBy(Cost),
            candidates.OrderByDescending(x => (double)x.ExpectedProfit / Math.Max(1UL, Cost(x))).ThenBy(Cost),
            candidates.OrderByDescending(x => x.ExpectedProfit / Math.Sqrt(Math.Max(1UL, Cost(x)))).ThenBy(Cost),
            candidates.OrderBy(Priority).ThenByDescending(x => (double)x.ExpectedProfit / Math.Max(1UL, Cost(x))).ThenBy(Cost),
        };
        var plans = new List<ProcurementPlan>();
        foreach (var strategy in strategies)
        {
            var itemSlots = owned.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
            var boughtUnits = new Dictionary<(uint, bool), ulong>();
            var orders = new List<ProcurementOrder>();
            ulong spent = 0, profit = 0;
            foreach (var candidate in strategy)
            {
                var key = (candidate.ItemId, candidate.IsHighQuality);
                var cost = Cost(candidate);
                var used = itemSlots.GetValueOrDefault(candidate.ItemId);
                if (orders.Count >= slotLimit || used >= rules[candidate.ItemId].MaximumSaleSlots ||
                    spent + cost > budget || quantityLimits is not null &&
                    boughtUnits.GetValueOrDefault(key) + candidate.Quantity > quantityLimits.GetValueOrDefault(key))
                    continue;
                orders.Add(candidate);
                itemSlots[candidate.ItemId] = used + 1;
                boughtUnits[key] = boughtUnits.GetValueOrDefault(key) + candidate.Quantity;
                spent += cost;
                profit += candidate.ExpectedProfit;
            }
            plans.Add(new(DateTimeOffset.UtcNow, orders, (uint)spent,
                (uint)Math.Min(profit, uint.MaxValue), orders.Count));
        }
        // Filling more sale slots beats a marginally richer plan that leaves them
        // empty, and among equal fills the higher-volume stock wins.
        return plans.OrderByDescending(x => x.Orders.Count)
            .ThenBy(x => x.Orders.Sum(Priority))
            .ThenByDescending(x => x.Orders.Sum(y => (long)y.ExpectedProfit))
            .ThenByDescending(x => x.Orders.Select(y => y.ItemId).Distinct().Count())
            .ThenBy(x => x.Orders.Select(y => y.WorldName).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenBy(x => x.TotalCost).First();
    }

    private static IEnumerable<bool> EligibleQualities(ProcurementRule rule, bool highQualityOnly)
    {
        foreach (var quality in new[] { false, true })
            if (ResaleStockPolicy.BuyableQuality(rule, quality, highQualityOnly))
                yield return quality;
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
