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

        var gates = request.Portfolio ?? PortfolioGates.Unrestricted;
        var rules = request.Rules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.First());
        var candidates = new List<ProcurementOrder>();
        var rejected = new List<PortfolioDecision>();
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

                var salesPerDay = SalesVelocityPolicy.DailyUnits(market, quality);
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

                    AddCandidate(candidates, rejected, gates, rule, new(
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
                        1, SalesPerDay: salesPerDay));
                }
            }
        }

        return Allocate(candidates, rejected, rules, request.GilBudget,
            Math.Min(request.FreeSaleSlots, request.FreeInventorySlots), request.BuyerFeePercent,
            request.OwnedStock ?? [], remainingUnits, gates, request.PortfolioCapacitySlots);
    }

    public ProcurementPlan BuildLiveMarketPlan(LiveMarketPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0 ||
            string.IsNullOrWhiteSpace(request.HomeWorld) || request.MarketTaxPercent is < 0 or > 100 ||
            request.BuyerFeePercent is < 0 or > 100 || request.MinimumRoiPercent is < 0 or > 1_000)
            return ProcurementPlan.Empty;

        var gates = request.Portfolio ?? PortfolioGates.Unrestricted;
        var rules = request.Rules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.First());
        var candidates = new List<ProcurementOrder>();
        var rejected = new List<PortfolioDecision>();

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

                var salesPerDay = SalesVelocityPolicy.DailyUnits(market, quality);
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
                    AddCandidate(candidates, rejected, gates, rule, new(
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
                        1, SalesPerDay: salesPerDay));
                }
            }
        }

        return Allocate(candidates, rejected, rules, request.GilBudget,
            Math.Min(request.FreeSaleSlots, request.FreeInventorySlots), request.BuyerFeePercent,
            request.OwnedStock ?? [], null, gates, request.PortfolioCapacitySlots);
    }

    /// <summary>
    /// Tier the candidate and apply the value gate. ROI has already been enforced by
    /// the price ceiling; this is the separate question of whether the deal is worth
    /// a whole retainer slot, which is what stops a 300% margin on a worthless dye.
    /// </summary>
    private static void AddCandidate(List<ProcurementOrder> candidates, List<PortfolioDecision> rejected,
        PortfolioGates gates, ProcurementRule rule, ProcurementOrder candidate)
    {
        var tier = PortfolioPolicy.ClassifyCandidate(rule.PreferredStock, candidate.SalesPerDay,
            candidate.ResaleValuePerSaleSlot, candidate.ExpectedProfitPerSaleSlot, gates);
        var tiered = candidate with { Tier = tier };
        if (tier != PortfolioTier.Core && tiered.ExpectedProfitPerSaleSlot < gates.MinimumProfitPerSaleSlot)
        {
            rejected.Add(Describe(tiered, false,
                $"insufficient slot value ({tiered.ExpectedProfitPerSaleSlot:N0} gil per sale slot, " +
                $"{gates.MinimumProfitPerSaleSlot:N0} required)"));
            return;
        }
        candidates.Add(tiered);
    }

    private static PortfolioDecision Describe(ProcurementOrder order, bool selected, string reason) => new(
        order.ItemId, order.ItemName, order.IsHighQuality, order.Tier, selected, reason,
        order.SalesPerDay, order.ExpectedProfit, order.RoiPercent, order.EstimatedDaysToSell,
        order.ExpectedResaleValue);

    private static ProcurementPlan Allocate(List<ProcurementOrder> candidates, List<PortfolioDecision> rejected,
        IReadOnlyDictionary<uint, ProcurementRule> rules, uint budget, int slotLimit, decimal buyerFee,
        IReadOnlyList<StockExposure> owned, IReadOnlyDictionary<(uint, bool), ulong>? quantityLimits,
        PortfolioGates gates, int capacitySlots)
    {
        ulong Cost(ProcurementOrder x) => PurchaseCost(x.PricePerUnit, x.Quantity, buyerFee);
        // Category preferences only ever break ties; actual value and demand decide.
        int Priority(ProcurementOrder x) => rules.TryGetValue(x.ItemId, out var rule) ? rule.TourPriority : int.MaxValue;

        var ownedSlots = owned.GroupBy(x => x.Tier).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
        var opening = PortfolioPolicy.Summarize(ownedSlots, slotLimit, capacitySlots, gates);
        if (candidates.Count == 0)
            return new(DateTimeOffset.UtcNow, [], 0, 0, 0, opening, rejected);

        // Absolute profit works well when slots are scarce; profit per gil can buy
        // more combinations with a small wallet. Every ordering is tier-major, so a
        // cheap opportunistic listing never displaces core stock in any of them.
        var strategies = new[]
        {
            Tiered(candidates).ThenByDescending(x => x.ProfitVelocity).ThenBy(Cost),
            Tiered(candidates).ThenByDescending(x => (double)x.ExpectedProfit).ThenBy(Cost),
            Tiered(candidates).ThenByDescending(x => (double)x.ExpectedProfit / Math.Max(1UL, Cost(x))).ThenBy(Cost),
            Tiered(candidates).ThenByDescending(x => x.SalesPerDay).ThenByDescending(x => x.ProfitVelocity).ThenBy(Cost),
            Tiered(candidates).ThenByDescending(x => x.ResaleValuePerSaleSlot).ThenBy(Priority).ThenBy(Cost),
        };

        var plans = new List<ProcurementPlan>();
        foreach (var strategy in strategies)
        {
            var itemSlots = owned.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
            var tierSlots = new Dictionary<PortfolioTier, int>(ownedSlots);
            var boughtUnits = new Dictionary<(uint, bool), ulong>();
            var orders = new List<ProcurementOrder>();
            var notes = new List<PortfolioDecision>();
            ulong spent = 0, profit = 0;
            foreach (var candidate in strategy)
            {
                var key = (candidate.ItemId, candidate.IsHighQuality);
                var cost = Cost(candidate);
                var used = itemSlots.GetValueOrDefault(candidate.ItemId);
                if (orders.Count >= slotLimit)
                {
                    notes.Add(Describe(candidate, false, "no free sale slot remains in this plan"));
                    continue;
                }
                // The opportunistic cap counts stock already listed, so a retainer
                // full of dye actively blocks buying more of it.
                if (candidate.Tier == PortfolioTier.Opportunistic &&
                    tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic) + candidate.SaleSlots >
                    opening.OpportunisticCap)
                {
                    notes.Add(Describe(candidate, false,
                        $"opportunistic portfolio cap reached ({tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic)}" +
                        $"/{opening.OpportunisticCap} slots)"));
                    continue;
                }
                if (used >= rules[candidate.ItemId].MaximumSaleSlots)
                {
                    notes.Add(Describe(candidate, false, $"already holding {used} sale slot(s) of this item"));
                    continue;
                }
                if (spent + cost > budget)
                {
                    notes.Add(Describe(candidate, false, "the remaining budget does not cover this stack"));
                    continue;
                }
                if (quantityLimits is not null &&
                    boughtUnits.GetValueOrDefault(key) + candidate.Quantity > quantityLimits.GetValueOrDefault(key))
                {
                    notes.Add(Describe(candidate, false, "the weekly market-share limit for this item is reached"));
                    continue;
                }
                orders.Add(candidate);
                notes.Add(Describe(candidate, true, SelectionReason(candidate, tierSlots, opening)));
                itemSlots[candidate.ItemId] = used + 1;
                tierSlots[candidate.Tier] = tierSlots.GetValueOrDefault(candidate.Tier) + candidate.SaleSlots;
                boughtUnits[key] = boughtUnits.GetValueOrDefault(key) + candidate.Quantity;
                spent += cost;
                profit += candidate.ExpectedProfit;
            }
            plans.Add(new(DateTimeOffset.UtcNow, orders, (uint)spent,
                (uint)Math.Min(profit, uint.MaxValue), orders.Count,
                PortfolioPolicy.Summarize(tierSlots, 0, capacitySlots, gates),
                notes));
        }

        // The lexicographic objective. Slot occupancy is deliberately last: a cheap
        // low-value item must never win merely by filling one more slot.
        var best = plans
            // 1. close the core/preferred deficit
            .OrderByDescending(x => Math.Min(TierSlots(x, PortfolioTier.Core), opening.CoreDeficit))
            // 2. never exceed the opportunistic cap
            .ThenBy(x => Math.Max(0, x.Summary.OpportunisticSlots - opening.OpportunisticCap))
            // 3. maximise high-quality, high-liquidity opportunity value
            .ThenByDescending(x => x.Orders.Where(o => o.Tier != PortfolioTier.Opportunistic).Sum(o => o.ProfitVelocity))
            // 4. maximise expected absolute profit
            .ThenByDescending(x => x.Orders.Sum(o => (long)o.ExpectedProfit))
            // 5. and only then use the remaining capacity
            .ThenByDescending(x => x.Orders.Count)
            .ThenByDescending(x => x.Orders.Select(o => o.ItemId).Distinct().Count())
            .ThenBy(x => x.Orders.Select(o => o.WorldName).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenBy(x => x.TotalCost)
            .First();
        return best with { Decisions = rejected.Concat(best.DecisionLog).ToArray() };
    }

    private static int TierSlots(ProcurementPlan plan, PortfolioTier tier) =>
        plan.Orders.Where(x => x.Tier == tier).Sum(x => x.SaleSlots);

    private static string SelectionReason(ProcurementOrder order,
        IReadOnlyDictionary<PortfolioTier, int> tierSlots, PortfolioAllocationSummary opening) => order.Tier switch
    {
        PortfolioTier.Core => tierSlots.GetValueOrDefault(PortfolioTier.Core) < opening.CoreTarget
            ? "core portfolio below target"
            : "core portfolio stock",
        PortfolioTier.Secondary => $"high-liquidity secondary stock ({order.SalesPerDay:N0} units/day, " +
                                   $"{order.ResaleValuePerSaleSlot:N0} gil per sale slot)",
        _ => $"opportunistic capacity available ({tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic)}" +
             $"/{opening.OpportunisticCap} slots)",
    };

    private static IOrderedEnumerable<ProcurementOrder> Tiered(IEnumerable<ProcurementOrder> candidates) =>
        candidates.OrderBy(x => PortfolioPolicy.Rank(x.Tier));

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
