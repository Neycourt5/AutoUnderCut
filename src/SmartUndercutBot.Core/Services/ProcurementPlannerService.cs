using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public interface IProcurementPlannerService
{
    ProcurementPlan BuildPlan(ProcurementPlanRequest request);
    ProcurementPlan BuildLiveMarketPlan(LiveMarketPlanRequest request);
}

/// <summary>
/// Turns market observations into a shopping list.
///
/// The objective is long-run realised gil, not percentage return. ROI is a safety
/// floor that answers "is this deal worth doing"; it never answers "which deal is
/// best". Once a listing clears its margin bar, candidates compete on how much gil
/// they generate per day of sale-slot occupancy, how much absolute profit they
/// carry, and whether the position is one the market's own demand can support.
/// </summary>
public sealed class ProcurementPlannerService : IProcurementPlannerService
{
    public ProcurementPlan BuildPlan(ProcurementPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fees = new FeeModel(request.MarketTaxPercent, request.BuyerFeePercent);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0 ||
            !fees.IsValid || request.MinimumRoiPercent is < 0 or > 1_000 ||
            request.MaximumWeeklySalesSharePercent is <= 0 or > 100)
            return ProcurementPlan.Empty;

        var gates = request.Portfolio ?? PortfolioGates.Unrestricted;
        var policy = request.Economics ?? ProcurementEconomicPolicy.Flat(request.MinimumRoiPercent);
        var rules = EnabledRules(request.Rules);
        var context = new PlanningContext(fees, policy, gates, rules);

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

                var salesPerDay = SalesVelocityPolicy.DailyUnits(market, quality);
                var ownedUnits = (ulong)(request.OwnedStock ?? []).Where(x =>
                    x.ItemId == market.ItemId && x.IsHighQuality == quality).Sum(x => (long)x.Quantity);

                // The weekly-share cap remains as a backstop for markets with no
                // usable velocity, where demand coverage cannot size a position.
                var observedLimit = Math.Max(
                    (ulong)Math.Max(1, rule.TargetStackSize),
                    (ulong)decimal.Floor(sales.Sum(x => (decimal)x.Quantity) *
                        request.MaximumWeeklySalesSharePercent / 100m));
                context.WeeklyShareLimits[(market.ItemId, quality)] =
                    observedLimit > ownedUnits ? observedLimit - ownedUnits : 0;
                context.OwnedUnits[(market.ItemId, quality)] = ownedUnits;

                var targetSalePrice = Median(sales.Select(x => x.PricePerUnit));
                if (!string.IsNullOrWhiteSpace(request.HomeWorld))
                {
                    var homeListings = (request.ResaleListings ?? market.Listings)
                        .Where(x => x.ItemId == market.ItemId && x.IsHighQuality == quality &&
                                    x.Quantity > 0 && x.PricePerUnit > 0 &&
                                    string.Equals(x.WorldName, request.HomeWorld, StringComparison.OrdinalIgnoreCase) &&
                                    request.OwnedRetainerIds?.Contains(x.RetainerId) != true)
                        .ToArray();
                    // Depth-aware: a handful of cheap units in a market that sells
                    // hundreds a day is absorbed long before our stack is reached,
                    // and must not redefine what the stack is worth. Real volume of
                    // cheap stock still moves the anchor.
                    var homeLowest = HomePriceReference.DepthAdjustedLowest(
                        homeListings, salesPerDay, policy.AnchorAbsorptionDays);
                    targetSalePrice = Math.Min(targetSalePrice, homeLowest > 1 ? homeLowest - 1 : 0);
                }
                if (targetSalePrice == 0)
                    continue;

                CollectCandidates(context, market, rule, quality, targetSalePrice, salesPerDay, ownedUnits,
                    request.MinimumProfitPerUnit, request.OwnedRetainerIds, requireWorldName: true);
            }
        }

        return Allocate(context, request.GilBudget,
            Math.Min(request.FreeSaleSlots, request.FreeInventorySlots),
            request.OwnedStock ?? [], request.PortfolioCapacitySlots, useWeeklyShare: true);
    }

    public ProcurementPlan BuildLiveMarketPlan(LiveMarketPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fees = new FeeModel(request.MarketTaxPercent, request.BuyerFeePercent);
        if (request.GilBudget == 0 || request.FreeSaleSlots <= 0 || request.FreeInventorySlots <= 0 ||
            string.IsNullOrWhiteSpace(request.HomeWorld) || !fees.IsValid ||
            request.MinimumRoiPercent is < 0 or > 1_000)
            return ProcurementPlan.Empty;

        var gates = request.Portfolio ?? PortfolioGates.Unrestricted;
        var policy = request.Economics ?? ProcurementEconomicPolicy.Flat(request.MinimumRoiPercent);
        var rules = EnabledRules(request.Rules);
        var context = new PlanningContext(fees, policy, gates, rules);

        foreach (var market in request.Markets)
        {
            if (!rules.TryGetValue(market.ItemId, out var rule))
                continue;

            foreach (var quality in EligibleQualities(rule, request.HighQualityOnly))
            {
                // The fallback tour deliberately ignores Universalis. Its resale anchor is
                // live in-game home-world stock, excluding the player's own retainers so a
                // self-listing cannot manufacture a deal.
                var homeListings = market.Listings
                    .Where(x => x.ItemId == market.ItemId && x.Quantity > 0 && x.IsHighQuality == quality)
                    .Where(x => string.Equals(x.WorldName, request.HomeWorld, StringComparison.OrdinalIgnoreCase))
                    .Where(x => x.PricePerUnit > 0 && !request.OwnedRetainerIds.Contains(x.RetainerId))
                    .ToArray();
                var salesPerDay = SalesVelocityPolicy.DailyUnits(market, quality);
                var targetSalePrice = HomePriceReference.DepthAdjustedLowest(
                    homeListings, salesPerDay, policy.AnchorAbsorptionDays);
                if (targetSalePrice == 0)
                    continue;

                var ownedUnits = (ulong)(request.OwnedStock ?? []).Where(x =>
                    x.ItemId == market.ItemId && x.IsHighQuality == quality).Sum(x => (long)x.Quantity);
                context.OwnedUnits[(market.ItemId, quality)] = ownedUnits;

                CollectCandidates(context, market, rule, quality, targetSalePrice, salesPerDay, ownedUnits,
                    request.MinimumProfitPerUnit, request.OwnedRetainerIds, requireWorldName: true);
            }
        }

        return Allocate(context, request.GilBudget,
            Math.Min(request.FreeSaleSlots, request.FreeInventorySlots),
            request.OwnedStock ?? [], request.PortfolioCapacitySlots, useWeeklyShare: false);
    }

    /// <summary>
    /// Price ceiling, then per-listing filtering, then costing. The margin bar is
    /// derived from what kind of market this is, and is applied as a maximum price -
    /// so ROI is enforced once, by the filter, and never re-derived downstream.
    /// </summary>
    private static void CollectCandidates(
        PlanningContext context, ProcurementMarketItem market, ProcurementRule rule, bool quality,
        uint targetSalePrice, decimal salesPerDay, ulong ownedUnits, uint minimumProfitPerUnit,
        IReadOnlySet<ulong>? ownedRetainerIds, bool requireWorldName)
    {
        var stackSize = (uint)Math.Max(1, rule.TargetStackSize);
        // Value per slot is a property of the market, not of one listing, so a full
        // target stack is what decides which margin bar applies. Otherwise a market
        // would qualify for the thin bar or not depending on which listing was seen.
        var referenceValuePerSlot = (ulong)targetSalePrice * stackSize;
        var recent = market.RecentSales.Where(x => x.IsHighQuality == quality && x.PricePerUnit > 0 &&
            x.Quantity > 0 && x.SoldAt >= DateTimeOffset.UtcNow.AddDays(-7) && x.SoldAt <= DateTimeOffset.UtcNow).ToArray();
        var reliableHistory = recent.Length >= 3;

        var requiredRoi = PortfolioPolicy.RequiredRoiPercent(
            context.Policy, rule.PreferredStock, salesPerDay, referenceValuePerSlot);
        if (!reliableHistory && context.Policy.AbsoluteMinimumRoiPercent > 0)
            requiredRoi = Math.Max(requiredRoi, context.Policy.StandardRoiPercent);
        var ceiling = context.Fees.MaximumUnitPrice(targetSalePrice, requiredRoi, minimumProfitPerUnit);
        if (rule.MaximumUnitPrice > 0)
            ceiling = Math.Min(ceiling, rule.MaximumUnitPrice);
        if (ceiling == 0)
            return;

        foreach (var listing in market.Listings)
        {
            if (listing.ItemId != market.ItemId || listing.PricePerUnit == 0 || listing.PricePerUnit > ceiling ||
                ownedRetainerIds?.Contains(listing.RetainerId) == true ||
                (requireWorldName && string.IsNullOrWhiteSpace(listing.WorldName)) ||
                listing.Quantity == 0 || listing.IsHighQuality != quality ||
                listing.Quantity > stackSize)
                continue;

            var landedCost = context.Fees.LandedCost(listing.PricePerUnit, listing.Quantity);
            var netProceeds = context.Fees.NetProceeds(targetSalePrice, listing.Quantity);
            if (landedCost > uint.MaxValue || netProceeds <= landedCost)
                continue;
            if (FeeModel.NetRoiPercent(netProceeds, landedCost) < requiredRoi ||
                netProceeds - landedCost < (ulong)minimumProfitPerUnit * listing.Quantity)
                continue;
            var expectedProfit = (uint)Math.Min(uint.MaxValue, netProceeds - landedCost);

            AddCandidate(context, rule, new(
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
                expectedProfit,
                1,
                SalesPerDay: salesPerDay,
                LandedCost: landedCost,
                RequiredRoiPercent: requiredRoi,
                OwnedUnitsBefore: ownedUnits,
                TargetUnits: InventoryCoveragePolicy.TargetUnits(
                    salesPerDay, context.Policy.CoverageDaysFor(PortfolioTier.Core))));
        }
    }

    /// <summary>
    /// Tier the candidate, apply the per-slot value gate, and score it. ROI is already
    /// enforced by the price ceiling; this is the separate question of whether the deal
    /// deserves a whole retainer slot, which is what stops a 300% margin on a trinket.
    /// </summary>
    private static void AddCandidate(PlanningContext context, ProcurementRule rule, ProcurementOrder candidate)
    {
        var tier = PortfolioPolicy.ClassifyCandidate(rule.PreferredStock, candidate.SalesPerDay,
            candidate.ResaleValuePerSaleSlot, candidate.ExpectedProfitPerSaleSlot, context.Gates, context.Policy);
        var tiered = candidate with
        {
            Tier = tier,
            TargetUnits = InventoryCoveragePolicy.TargetUnits(
                candidate.SalesPerDay, context.Policy.CoverageDaysFor(tier)),
        };
        if (tier != PortfolioTier.Core && tiered.ExpectedProfitPerSaleSlot < context.Gates.MinimumProfitPerSaleSlot)
        {
            context.Rejected.Add(Describe(tiered, false,
                $"insufficient slot value ({tiered.ExpectedProfitPerSaleSlot:N0} gil, " +
                $"{context.Gates.MinimumProfitPerSaleSlot:N0} required)"));
            return;
        }
        // Preference is a weight on the economics, never a bypass of them: an
        // outstanding non-core opportunity can still beat a poor core one.
        context.Candidates.Add(tiered with
        {
            AllocationScore = tiered.ExpectedGilPerDay * context.Policy.ScoreWeightFor(tier),
        });
    }

    private static PortfolioDecision Describe(ProcurementOrder order, bool selected, string reason) => new(
        order.ItemId, order.ItemName, order.IsHighQuality, order.Tier, selected, reason,
        order.SalesPerDay, order.ExpectedProfit, order.NetRoiPercent, order.EstimatedDaysToSell,
        order.ExpectedResaleValue, order.CapitalAtRisk, order.ExpectedNetProceeds,
        order.ExpectedGilPerDay, order.InventoryCoverageDays, order.CoverageDaysAfterPurchase, order.Quantity);

    /// <summary>
    /// One greedy pass over the candidates in economic order.
    ///
    /// The ordering is the objective: expected gil per day first (weighted by how much
    /// confidence the market's class earns), then absolute profit, then liquidity, then
    /// cost. Every rejection below is a capacity or concentration limit, not a
    /// preference - preference has already been expressed in the score.
    /// </summary>
    private static ProcurementPlan Allocate(
        PlanningContext context, uint budget, int slotLimit,
        IReadOnlyList<StockExposure> owned, int capacitySlots, bool useWeeklyShare)
    {
        var policy = context.Policy;
        var ownedSlots = owned.GroupBy(x => x.Tier).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
        var opening = PortfolioPolicy.Summarize(ownedSlots, slotLimit, capacitySlots, context.Gates);
        if (context.Candidates.Count == 0)
            return new(DateTimeOffset.UtcNow, [], 0, 0, 0, opening, context.Rejected);

        int Priority(ProcurementOrder x) =>
            context.Rules.TryGetValue(x.ItemId, out var rule) ? rule.TourPriority : int.MaxValue;

        var ordered = context.Candidates
            .OrderByDescending(x => x.AllocationScore)
            .ThenByDescending(x => x.ExpectedProfit)
            .ThenBy(x => x.EstimatedDaysToSell)
            .ThenByDescending(x => x.CapitalAtRisk)
            .ThenBy(Priority)
            .ThenBy(x => x.ItemId)
            .ThenBy(x => x.ListingId)
            .ToArray();

        var itemSlots = owned.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
        var tierSlots = new Dictionary<PortfolioTier, int>(ownedSlots);
        var heldUnits = new Dictionary<(uint, bool), ulong>(context.OwnedUnits);
        var boughtUnits = new Dictionary<(uint, bool), ulong>();
        var orders = new List<ProcurementOrder>();
        var notes = new List<PortfolioDecision>();
        ulong spent = 0, profit = 0, opportunisticSpent = 0;

        foreach (var candidate in ordered)
        {
            var key = (candidate.ItemId, candidate.IsHighQuality);
            var cost = candidate.CapitalAtRisk;
            var rule = context.Rules[candidate.ItemId];
            var held = heldUnits.GetValueOrDefault(key);

            if (orders.Count >= slotLimit)
            {
                notes.Add(Describe(candidate, false, "no free sale slot remains"));
                continue;
            }
            // Counts stock already listed, so a retainer full of trinkets actively
            // blocks buying more of them.
            if (candidate.Tier == PortfolioTier.Opportunistic &&
                tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic) + candidate.SaleSlots >
                opening.OpportunisticCap)
            {
                notes.Add(Describe(candidate, false,
                    $"opportunistic portfolio cap reached ({tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic)}" +
                    $"/{opening.OpportunisticCap} slots)"));
                continue;
            }
            if (candidate.Tier == PortfolioTier.Opportunistic &&
                opportunisticSpent + cost > budget * context.Gates.OpportunisticMaximumPercent / 100m)
            {
                notes.Add(Describe(candidate, false, "opportunistic capital cap reached"));
                continue;
            }
            var maximumSlots = EffectiveMaximumSlots(policy, rule, candidate);
            if (itemSlots.GetValueOrDefault(candidate.ItemId) >= maximumSlots)
            {
                notes.Add(Describe(candidate, false,
                    $"already holding {itemSlots.GetValueOrDefault(candidate.ItemId)} of {maximumSlots} sale slots for this item"));
                continue;
            }
            if (spent + cost > budget)
            {
                notes.Add(Describe(candidate, false, "remaining budget does not cover this stack"));
                continue;
            }

            var coverageDays = policy.CoverageDaysFor(candidate.Tier);
            if (coverageDays > 0)
            {
                if (!InventoryCoveragePolicy.CanAdd(held, candidate.Quantity, candidate.SalesPerDay,
                        coverageDays, policy.CoverageOvershootDays))
                {
                    var have = InventoryCoveragePolicy.CoverageDays(held, candidate.SalesPerDay);
                    notes.Add(Describe(candidate with { OwnedUnitsBefore = held }, false,
                        candidate.SalesPerDay <= 0
                            ? "no reliable sales velocity to size a position against"
                            : $"demand coverage reached ({have:N1}d held, {coverageDays:N1}d target)"));
                    continue;
                }
            }
            else if (useWeeklyShare &&
                     boughtUnits.GetValueOrDefault(key) + candidate.Quantity >
                     context.WeeklyShareLimits.GetValueOrDefault(key))
            {
                notes.Add(Describe(candidate, false, "weekly market-share limit reached for this item"));
                continue;
            }

            var selected = candidate with { OwnedUnitsBefore = held };
            orders.Add(selected);
            notes.Add(Describe(selected, true, SelectionReason(selected, coverageDays)));
            itemSlots[candidate.ItemId] = itemSlots.GetValueOrDefault(candidate.ItemId) + 1;
            tierSlots[candidate.Tier] = tierSlots.GetValueOrDefault(candidate.Tier) + candidate.SaleSlots;
            boughtUnits[key] = boughtUnits.GetValueOrDefault(key) + candidate.Quantity;
            heldUnits[key] = held + candidate.Quantity;
            spent += cost;
            if (candidate.Tier == PortfolioTier.Opportunistic) opportunisticSpent += cost;
            profit += candidate.ExpectedProfit;
        }

        return new(DateTimeOffset.UtcNow, orders, (uint)Math.Min(spent, uint.MaxValue),
            (uint)Math.Min(profit, uint.MaxValue), orders.Count,
            PortfolioPolicy.Summarize(tierSlots, 0, capacitySlots, context.Gates),
            context.Rejected.Concat(notes).ToArray());
    }

    /// <summary>
    /// How many sale slots one item may occupy. The fixed per-rule number is a floor,
    /// not a ceiling: a market whose own demand supports more inventory is allowed
    /// more, up to a hard emergency limit that no amount of demand can exceed.
    /// </summary>
    public static int EffectiveMaximumSlots(
        ProcurementEconomicPolicy policy, ProcurementRule rule, ProcurementOrder candidate)
    {
        var days = policy.CoverageDaysFor(candidate.Tier);
        if (days <= 0)
            return rule.MaximumSaleSlots;
        var demandSlots = InventoryCoveragePolicy.DemandJustifiedSlots(
            candidate.SalesPerDay, days + policy.CoverageOvershootDays,
            Math.Max(1, rule.TargetStackSize), policy.EmergencyMaximumSlotsPerItem);
        return Math.Min(policy.EmergencyMaximumSlotsPerItem, Math.Max(rule.MaximumSaleSlots, demandSlots));
    }

    private static string SelectionReason(ProcurementOrder order, decimal coverageDays)
    {
        var basis = order.Tier switch
        {
            PortfolioTier.Core => "preferred high-volume stock",
            PortfolioTier.Secondary => "high-liquidity secondary stock",
            _ => "opportunistic capacity available",
        };
        return coverageDays > 0
            ? $"selected {basis}; restocking toward {coverageDays:N1}d coverage"
            : basis;
    }

    private static Dictionary<uint, ProcurementRule> EnabledRules(IReadOnlyList<ProcurementRule> rules) =>
        rules.Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.First());

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

    /// <summary>Everything one planning run accumulates, so the two entry points share a shape.</summary>
    private sealed class PlanningContext(
        FeeModel fees, ProcurementEconomicPolicy policy, PortfolioGates gates,
        IReadOnlyDictionary<uint, ProcurementRule> rules)
    {
        public FeeModel Fees { get; } = fees;
        public ProcurementEconomicPolicy Policy { get; } = policy;
        public PortfolioGates Gates { get; } = gates;
        public IReadOnlyDictionary<uint, ProcurementRule> Rules { get; } = rules;
        public List<ProcurementOrder> Candidates { get; } = [];
        public List<PortfolioDecision> Rejected { get; } = [];
        public Dictionary<(uint, bool), ulong> WeeklyShareLimits { get; } = [];
        public Dictionary<(uint, bool), ulong> OwnedUnits { get; } = [];
    }
}
