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
        var context = new PlanningContext(fees, policy, gates, rules)
        {
            NonPreferredGilBudget = request.NonPreferredGilBudget,
            NonPreferredSaleSlots = request.NonPreferredSaleSlots,
        };

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
                var shareKey = (market.ItemId, quality);
                var remainingShare = observedLimit > ownedUnits ? observedLimit - ownedUnits : 0;
                // Two market entries can name the same item. The limit is a cap on a
                // single position, so the tighter of the two is the honest answer;
                // letting the last one seen win could silently raise it.
                context.WeeklyShareLimits[shareKey] = context.WeeklyShareLimits.TryGetValue(shareKey, out var existing)
                    ? Math.Min(existing, remainingShare)
                    : remainingShare;
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
        var context = new PlanningContext(fees, policy, gates, rules)
        {
            NonPreferredGilBudget = request.NonPreferredGilBudget,
            NonPreferredSaleSlots = request.NonPreferredSaleSlots,
        };

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
        // No score is stored here. What a stack is worth depends on the inventory it
        // will land behind, and that is only known during allocation - so the score
        // is computed there, per candidate, per iteration.
        context.Candidates.Add(tiered);
    }

    /// <summary>
    /// What one more stack is worth right now: gil per day of committed capital given
    /// the units already queued in front of it, weighted by the market's class.
    ///
    /// Preference is a weight on the economics, never a bypass of them - an
    /// outstanding non-core opportunity can still beat a poor core one.
    /// </summary>
    private static decimal ScoreOf(ProcurementOrder candidate, ProcurementEconomicPolicy policy) =>
        candidate.MarginalGilPerDay * policy.ScoreWeightFor(candidate.Tier);

    private static PortfolioDecision Describe(ProcurementOrder order, bool selected, string reason) => new(
        order.ItemId, order.ItemName, order.IsHighQuality, order.Tier, selected, reason,
        order.SalesPerDay, order.ExpectedProfit, order.NetRoiPercent, order.EstimatedDaysToSell,
        order.ExpectedResaleValue, order.CapitalAtRisk, order.ExpectedNetProceeds,
        order.ExpectedGilPerDay, order.InventoryCoverageDays, order.CoverageDaysAfterPurchase, order.Quantity,
        order.MarginalGilPerDay, order.MarginalDaysToClear, order.AllocationScore);

    /// <summary>
    /// Bounded search budget for the constrained-capital improvement pass. Greedy is
    /// still the allocator; this only re-runs it a handful of times.
    /// </summary>
    private const int MaximumImprovementDrops = 8;
    private const int MaximumImprovementRounds = 3;

    /// <summary>
    /// Choose the basket.
    ///
    /// Greedy selection on marginal value, then a small bounded repair step for the
    /// one case greedy is known to get wrong: a tight gil budget where one expensive
    /// stack crowds out two cheaper ones that are together worth more.
    /// </summary>
    private static ProcurementPlan Allocate(
        PlanningContext context, uint budget, int slotLimit,
        IReadOnlyList<StockExposure> owned, int capacitySlots, bool useWeeklyShare)
    {
        var ownedSlots = owned.GroupBy(x => x.Tier).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
        var opening = PortfolioPolicy.Summarize(ownedSlots, slotLimit, capacitySlots, context.Gates);
        if (context.Candidates.Count == 0)
            return new(DateTimeOffset.UtcNow, [], 0, 0, 0, opening, context.Rejected);

        var outcome = SelectGreedily(context, budget, slotLimit, owned, ownedSlots, opening, useWeeklyShare, null);
        outcome = ImproveUnderConstrainedCapital(
            context, budget, slotLimit, owned, ownedSlots, opening, useWeeklyShare, outcome);

        return new(DateTimeOffset.UtcNow, outcome.Orders,
            (uint)Math.Min(outcome.Spent, uint.MaxValue),
            (uint)Math.Min(outcome.Profit, uint.MaxValue), outcome.Orders.Count,
            PortfolioPolicy.Summarize(outcome.TierSlots, 0, capacitySlots, context.Gates),
            context.Rejected.Concat(outcome.Notes).ToArray());
    }

    /// <summary>
    /// Repeated best-choice selection.
    ///
    /// Each iteration re-scores every surviving candidate against the inventory that
    /// now exists - including everything selected earlier in this same pass - and
    /// takes the best one. That is the whole of the marginal model: the second stack
    /// of a market is scored over the days it will actually take to clear behind the
    /// first, so a market's attractiveness decays as the position fills and other
    /// opportunities overtake it without any category quota saying that they must.
    ///
    /// Every rejection below is a capacity or concentration limit, and all of them
    /// are monotone - holdings, spend and slots only ever grow - so a candidate that
    /// fails one can never become feasible later and is dropped for good. Each
    /// improvement trial starts afresh from context.Candidates, so dropped candidates
    /// are available again when a trial frees capital or coverage.
    /// </summary>
    private static AllocationOutcome SelectGreedily(
        PlanningContext context, uint budget, int slotLimit, IReadOnlyList<StockExposure> owned,
        IReadOnlyDictionary<PortfolioTier, int> ownedSlots, PortfolioAllocationSummary opening,
        bool useWeeklyShare, IReadOnlyDictionary<(uint ItemId, bool IsHighQuality), int>? trialCaps)
    {
        var policy = context.Policy;
        var itemSlots = owned.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
        var tierSlots = new Dictionary<PortfolioTier, int>(ownedSlots);
        var heldUnits = new Dictionary<(uint, bool), ulong>(context.OwnedUnits);
        var boughtUnits = new Dictionary<(uint, bool), ulong>();
        var orders = new List<ProcurementOrder>();
        var notes = new List<PortfolioDecision>();
        ulong spent = 0, profit = 0, opportunisticSpent = 0;
        ulong nonPreferredSpent = 0;
        var nonPreferredSlots = 0;
        decimal objective = 0;
        var budgetBlocked = false;

        var opportunisticCapital = budget * context.Gates.OpportunisticMaximumPercent / 100m;
        var remaining = context.Candidates.ToList();
        // Stacks added by this pass, per market, so a trial cap can say "give this
        // market one fewer slot" without depending on which listing was picked.
        var addedByKey = new Dictionary<(uint, bool), int>();

        int Priority(ProcurementOrder x) =>
            context.Rules.TryGetValue(x.ItemId, out var rule) ? rule.TourPriority : int.MaxValue;

        while (orders.Count < slotLimit && remaining.Count > 0)
        {
            ProcurementOrder? best = null;
            var survivors = new List<ProcurementOrder>(remaining.Count);

            foreach (var pending in remaining)
            {
                var key = (pending.ItemId, pending.IsHighQuality);
                var held = heldUnits.GetValueOrDefault(key);
                var candidate = pending with { OwnedUnitsBefore = held };
                candidate = candidate with { AllocationScore = ScoreOf(candidate, policy) };
                var cost = candidate.CapitalAtRisk;
                var rule = context.Rules[candidate.ItemId];
                var coverageDays = policy.CoverageDaysFor(candidate.Tier);

                // Counts stock already listed, so a retainer full of trinkets
                // actively blocks buying more of them.
                if (candidate.Tier == PortfolioTier.Opportunistic &&
                    tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic) + candidate.SaleSlots >
                    opening.OpportunisticCap)
                {
                    notes.Add(Describe(candidate, false,
                        $"opportunistic portfolio cap reached ({tierSlots.GetValueOrDefault(PortfolioTier.Opportunistic)}" +
                        $"/{opening.OpportunisticCap} slots)"));
                    continue;
                }
                if (candidate.Tier == PortfolioTier.Opportunistic && opportunisticSpent + cost > opportunisticCapital)
                {
                    notes.Add(Describe(candidate, false, "opportunistic capital cap reached"));
                    continue;
                }
                if (trialCaps is not null && trialCaps.TryGetValue(key, out var trialCap) &&
                    addedByKey.GetValueOrDefault(key) >= trialCap)
                {
                    notes.Add(Describe(candidate with { OwnedUnitsBefore = held }, false,
                        "held back so cheaper stock could use the gil"));
                    continue;
                }
                var maximumSlots = EffectiveMaximumSlots(policy, rule, candidate);
                if (itemSlots.GetValueOrDefault(candidate.ItemId) >= maximumSlots)
                {
                    notes.Add(Describe(candidate, false,
                        $"already holding {itemSlots.GetValueOrDefault(candidate.ItemId)} of {maximumSlots} sale slots for this item"));
                    continue;
                }
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
                if (!rule.PreferredStock &&
                    ((context.NonPreferredGilBudget is { } otherBudget && nonPreferredSpent + cost > otherBudget) ||
                     (context.NonPreferredSaleSlots is { } otherSlots && nonPreferredSlots + candidate.SaleSlots > otherSlots)))
                {
                    if (context.NonPreferredGilBudget is { } constrainedBudget && nonPreferredSpent + cost > constrainedBudget)
                        budgetBlocked = true;
                    notes.Add(Describe(candidate, false, "non-preferred spare-stock allowance reached; remaining capacity is for preferred stock"));
                    continue;
                }
                if (spent + cost > budget)
                {
                    // Spending only grows in this pass. A trial starts afresh, so
                    // there is no need to reconsider or log this rejection on every pick.
                    budgetBlocked = true;
                    notes.Add(Describe(candidate with { OwnedUnitsBefore = held }, false,
                        "remaining budget does not cover this stack"));
                    continue;
                }

                survivors.Add(candidate);
                if (best is null || IsBetter(candidate, best, Priority))
                    best = candidate;
            }

            if (best is null)
            {
                remaining = [];
                break;
            }

            var chosen = best;
            orders.Add(chosen);
            notes.Add(Describe(chosen, true, SelectionReason(chosen, policy.CoverageDaysFor(chosen.Tier))));
            var chosenKey = (chosen.ItemId, chosen.IsHighQuality);
            itemSlots[chosen.ItemId] = itemSlots.GetValueOrDefault(chosen.ItemId) + 1;
            tierSlots[chosen.Tier] = tierSlots.GetValueOrDefault(chosen.Tier) + chosen.SaleSlots;
            boughtUnits[chosenKey] = boughtUnits.GetValueOrDefault(chosenKey) + chosen.Quantity;
            addedByKey[chosenKey] = addedByKey.GetValueOrDefault(chosenKey) + 1;
            heldUnits[chosenKey] = heldUnits.GetValueOrDefault(chosenKey) + chosen.Quantity;
            spent += chosen.CapitalAtRisk;
            if (!context.Rules[chosen.ItemId].PreferredStock)
            {
                nonPreferredSpent += chosen.CapitalAtRisk;
                nonPreferredSlots += chosen.SaleSlots;
            }
            if (chosen.Tier == PortfolioTier.Opportunistic) opportunisticSpent += chosen.CapitalAtRisk;
            profit += chosen.ExpectedProfit;
            objective += chosen.AllocationScore;

            // Survivors were scored against the inventory of this iteration; they are
            // re-scored on the next one. Only the chosen listing leaves the pool.
            remaining = survivors.Where(x => !SameListing(x, chosen)).ToList();
        }

        foreach (var leftover in remaining)
        {
            var final = leftover with
            { OwnedUnitsBefore = heldUnits.GetValueOrDefault((leftover.ItemId, leftover.IsHighQuality)) };
            final = final with { AllocationScore = ScoreOf(final, policy) };
            notes.Add(Describe(final, false, "no free sale slot remains"));
        }

        return new(orders, notes, spent, profit, objective, tierSlots, budgetBlocked);
    }

    /// <summary>
    /// The repair step for constrained capital.
    ///
    /// Greedy takes the highest-scoring stack it can afford, which under a tight
    /// budget can spend on one candidate worth 300k what would have bought two worth
    /// 200k each. Rather than build an optimiser, drop one selected stack at a time -
    /// the expensive ones first, since those are what crowd the rest out - and re-run
    /// the same greedy selection without it. A replacement basket is adopted only if
    /// the total objective strictly improves, and it is produced by that same
    /// routine, so every budget, slot, coverage, concentration, opportunistic and
    /// rule constraint is enforced identically.
    ///
    /// Bounded at 2 x MaximumImprovementRounds x MaximumImprovementDrops re-runs, and
    /// skipped entirely unless the budget actually stopped something - so a large
    /// wallet pays nothing for it and behaves exactly as it did before.
    /// </summary>
    private static AllocationOutcome ImproveUnderConstrainedCapital(
        PlanningContext context, uint budget, int slotLimit, IReadOnlyList<StockExposure> owned,
        IReadOnlyDictionary<PortfolioTier, int> ownedSlots, PortfolioAllocationSummary opening,
        bool useWeeklyShare, AllocationOutcome current)
    {
        var caps = new Dictionary<(uint, bool), int>();
        for (var round = 0; round < MaximumImprovementRounds; round++)
        {
            if (!current.BudgetBlocked || current.Orders.Count == 0)
                break;

            AllocationOutcome? bestAlternative = null;
            (uint, bool) bestKey = default;
            var bestCap = 0;

            // The neighbourhood: for each market in the basket, what if it were
            // allowed one fewer stack, or none at all? Expressed as a slot cap
            // rather than as a banned listing, because banning one listing of a
            // market that has identical siblings changes nothing - the sibling
            // simply takes its place, and the trial is wasted.
            var groups = current.Orders
                .GroupBy(x => (x.ItemId, x.IsHighQuality))
                .Select(g => (Key: g.Key, Stacks: g.Count(), Capital: g.Sum(x => (decimal)x.CapitalAtRisk)))
                .OrderByDescending(g => g.Capital)
                .ThenBy(g => g.Key.ItemId)
                .ThenBy(g => g.Key.IsHighQuality)
                .Take(MaximumImprovementDrops)
                .ToArray();

            foreach (var group in groups)
            {
                foreach (var cap in group.Stacks > 1 ? new[] { group.Stacks - 1, 0 } : [0])
                {
                    if (caps.TryGetValue(group.Key, out var standing) && standing <= cap)
                        continue;
                    var trial = new Dictionary<(uint, bool), int>(caps) { [group.Key] = cap };
                    var alternative = SelectGreedily(
                        context, budget, slotLimit, owned, ownedSlots, opening, useWeeklyShare, trial);
                    if (alternative.Objective <= current.Objective)
                        continue;
                    if (bestAlternative is null || IsBetterBasket(alternative, bestAlternative))
                    {
                        bestAlternative = alternative;
                        bestKey = group.Key;
                        bestCap = cap;
                    }
                }
            }

            if (bestAlternative is null)
                break;
            caps[bestKey] = bestCap;
            current = bestAlternative;
        }
        return current;
    }

    /// <summary>Total order over candidates, so selection is deterministic.</summary>
    private static bool IsBetter(ProcurementOrder x, ProcurementOrder y, Func<ProcurementOrder, int> priority)
    {
        if (x.AllocationScore != y.AllocationScore) return x.AllocationScore > y.AllocationScore;
        if (x.ExpectedProfit != y.ExpectedProfit) return x.ExpectedProfit > y.ExpectedProfit;
        if (x.MarginalDaysToClear != y.MarginalDaysToClear) return x.MarginalDaysToClear < y.MarginalDaysToClear;
        // Deploying more capital productively is a late tie-break, never a reason.
        if (x.CapitalAtRisk != y.CapitalAtRisk) return x.CapitalAtRisk > y.CapitalAtRisk;
        var px = priority(x);
        var py = priority(y);
        if (px != py) return px < py;
        if (x.ItemId != y.ItemId) return x.ItemId < y.ItemId;
        if (x.IsHighQuality != y.IsHighQuality) return !x.IsHighQuality;
        if (x.ListingId != y.ListingId) return x.ListingId < y.ListingId;
        return string.CompareOrdinal(x.WorldName, y.WorldName) < 0;
    }

    private static bool SameListing(ProcurementOrder x, ProcurementOrder y) =>
        x.ItemId == y.ItemId && x.IsHighQuality == y.IsHighQuality && x.ListingId == y.ListingId &&
        string.Equals(x.WorldName, y.WorldName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Total order over complete baskets, so the repair step is deterministic.</summary>
    private static bool IsBetterBasket(AllocationOutcome x, AllocationOutcome y)
    {
        if (x.Objective != y.Objective) return x.Objective > y.Objective;
        if (x.Profit != y.Profit) return x.Profit > y.Profit;
        if (x.Spent != y.Spent) return x.Spent < y.Spent;
        return x.Orders.Count > y.Orders.Count;
    }

    /// <summary>One complete candidate basket and what it is worth.</summary>
    private sealed record AllocationOutcome(
        IReadOnlyList<ProcurementOrder> Orders,
        IReadOnlyList<PortfolioDecision> Notes,
        ulong Spent,
        ulong Profit,
        // Sum of the marginal, class-weighted gil per day of every selected stack,
        // evaluated in the order it was selected. This is what the repair step
        // maximises, so the two passes optimise the same quantity.
        decimal Objective,
        IReadOnlyDictionary<PortfolioTier, int> TierSlots,
        bool BudgetBlocked);

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
        public uint? NonPreferredGilBudget { get; init; }
        public int? NonPreferredSaleSlots { get; init; }
        public List<ProcurementOrder> Candidates { get; } = [];
        public List<PortfolioDecision> Rejected { get; } = [];
        public Dictionary<(uint, bool), ulong> WeeklyShareLimits { get; } = [];
        public Dictionary<(uint, bool), ulong> OwnedUnits { get; } = [];
    }
}
