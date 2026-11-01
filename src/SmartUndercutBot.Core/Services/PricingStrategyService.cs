using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public interface IPricingStrategyService
{
    PriceDecision Evaluate(PricingContext context);
}

public sealed class PricingStrategyService : IPricingStrategyService
{
    public const uint MaximumListingPrice = 999_999_999;

    public PriceDecision Evaluate(PricingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var listing = context.Listing;
        var market = context.Market;
        var rule = context.Rule;

        if (listing.ItemId == 0 || market.ItemId != listing.ItemId ||
            listing.CurrentPrice is 0 or > MaximumListingPrice ||
            rule.PriceWarDropPercent is < 0 or >= 100 || rule.MinimumMarginPercent < 0 ||
            rule.PercentageTolerance < 0 || context.Fees is { IsValid: false })
        {
            return Decision(PriceDecisionKind.InvalidData, listing, null, 0, "Listing or rule data is outside valid bounds.");
        }

        var floor = PositionCostPolicy.MinimumListingPrice(rule, context.Fees, listing.AcquisitionCost);
        if (floor > MaximumListingPrice)
            return Decision(PriceDecisionKind.InvalidData, listing, null, floor,
                "The effective minimum cannot be reached within the game's listing-price limit.");
        var ownedRetainers = context.OwnedRetainerIds;
        var competitors = market.Listings
            .Where(x => x.Quantity > 0)
            .Where(x => x.PricePerUnit is > 0 and <= MaximumListingPrice)
            .Where(x => IsQualityAllowed(x.IsHighQuality, listing.IsHighQuality, rule.QualityFilter))
            .Where(x => x.RetainerId == 0 ||
                        (ownedRetainers is not null
                            ? !ownedRetainers.Contains(x.RetainerId)
                            : x.RetainerId != listing.RetainerId))
            .Where(x => x.RetainerId != 0 || string.IsNullOrWhiteSpace(x.RetainerName) ||
                        !string.Equals(x.RetainerName, listing.RetainerName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (competitors.Length == 0)
            return RaiseAsSoleSeller(context) ??
                   Decision(PriceDecisionKind.NoMarketData, listing, null, floor,
                       $"No competitor listings matched {QualityDescription(rule.QualityFilter, listing.IsHighQuality)}.");

        // Depth-aware: a one-unit undercut in a market that turns over hundreds of
        // units a day is gone long before our stack is reached, and chasing it just
        // walks the whole position down for nothing. Real competing volume still
        // sets the price. With no absorption figure this is the cheapest listing.
        var lowest = DepthAdjustedLowest(competitors, market.AbsorbableUnits);
        var rawTarget = rule.Mode == PricingMode.MatchLowest
            ? lowest
            : lowest > rule.UndercutAmount ? lowest - rule.UndercutAmount : 1u;
        // Matching the market would mean selling under what this stock cost.
        var belowCost = rawTarget < floor;
        var hasKnownCost = listing.AcquisitionCost > 0 || rule.CostBasis > 0;
        // When the sale history did not arrive, our own asking price is the only
        // reference left. Without it the guard failed open: stock with no known cost
        // followed a one-gil troll listing straight down.
        var reference = market.HistoricalMedianPrice is > 0 ? market.HistoricalMedianPrice : listing.CurrentPrice;
        // History can flag a large move, but it must not block selling known-cost
        // stock for a profit. Unknown-cost stock retains the historical safeguard.
        // A crash also comes before every stuck-stock and raise rule below: stock is
        // never sold under cost into one, and nothing is raised during one.
        if (!(hasKnownCost && rawTarget >= floor) &&
            IsPriceWar(lowest, reference, rule.PriceWarDropPercent))
        {
            if (rule.PriceWarAction == PriceWarAction.LeaveUnchanged)
                return Decision(PriceDecisionKind.PriceWar, listing, lowest, floor,
                    market.HistoricalMedianPrice is > 0
                        ? "Lowest price breached the configured historical drop threshold."
                        : "No sale history arrived and the lowest price is far below this listing; holding instead of following it.",
                    belowCost);

            var protectedFloor = Math.Max(floor, ProtectedHistoricalFloor(reference!.Value, rule.PriceWarDropPercent));
            if (listing.CurrentPrice == protectedFloor)
                return Decision(PriceDecisionKind.NoChange, listing, lowest, protectedFloor,
                    "Listing already matches the protected historical floor.", belowCost);

            return new PriceDecision(PriceDecisionKind.Update, listing.CurrentPrice, protectedFloor, lowest, protectedFloor,
                "Price war detected; using the protected historical floor.", belowCost);
        }

        // Below cost, the stuck-stock policy lists at break-even straight away, and
        // once the market has stayed there long enough, down to the accepted loss.
        var effectiveFloor = floor;
        if (belowCost && context.StuckStock is { } stuck && RelaxationDue(context, stuck))
            effectiveFloor = PositionCostPolicy.RelaxedListingPrice(
                rule, context.Fees, listing.AcquisitionCost, stuck.MaximumLossPercent);
        // Where this listing belongs: just under the competition, never under its floor.
        var desired = Math.Max(effectiveFloor, RoundDown(rawTarget, rule.Rounding));

        if (context.Raise is { } raise)
        {
            // Known-cost stock above its floor skips the crash check above, so it is
            // repeated here: nothing is ever raised into a collapsing market.
            if (listing.CurrentPrice < desired && !IsPriceWar(lowest, reference, rule.PriceWarDropPercent))
                return RaiseTowardFront(context, competitors, floor, desired, belowCost, raise);
            // Strictly cheaper than every competitor is already the front of the
            // queue; only a tie or a dearer ask still needs the undercut.
            if (listing.CurrentPrice < lowest)
                return Decision(PriceDecisionKind.NoChange, listing, lowest, effectiveFloor,
                    $"Current listing is already the cheapest in {QualityDescription(rule.QualityFilter, listing.IsHighQuality)}.",
                    belowCost);
        }
        else if (listing.CurrentPrice <= lowest)
            return Decision(PriceDecisionKind.NoChange, listing, lowest, floor,
                $"Current listing is already at or below the lowest competitor in {QualityDescription(rule.QualityFilter, listing.IsHighQuality)}.",
                belowCost);

        if (WithinTolerance(listing.CurrentPrice, lowest, rule))
            return Decision(PriceDecisionKind.WithinTolerance, listing, lowest, floor,
                "Current price is inside the configured tolerance band.", belowCost);

        if (belowCost)
        {
            if (context.StuckStock is null || desired >= listing.CurrentPrice)
                return Decision(PriceDecisionKind.BelowFloor, listing, lowest, effectiveFloor,
                    "Competitive target would fall below the effective minimum price.", true);
            return new PriceDecision(PriceDecisionKind.Update, listing.CurrentPrice, desired, lowest, effectiveFloor,
                effectiveFloor < floor
                    ? $"The market has stayed below this stock's cost for {HoursBelowCost(context):N0}h; following it " +
                      $"down to at most {context.StuckStock.MaximumLossPercent:N0}% under cost."
                    : "The market is below this stock's cost; listing at break-even instead of holding the old price.",
                true);
        }

        var target = desired;
        if (target > MaximumListingPrice)
            return Decision(PriceDecisionKind.InvalidData, listing, lowest, floor, "Calculated target exceeds the game's listing-price limit.");

        if (target == listing.CurrentPrice)
            return Decision(PriceDecisionKind.NoChange, listing, lowest, floor, "Calculated target equals the current price.");

        return new PriceDecision(PriceDecisionKind.Update, listing.CurrentPrice, target, lowest, floor,
            $"{rule.Mode} target calculated from the lowest valid competitor price.");
    }

    private static PriceDecision Decision(
        PriceDecisionKind kind, RetainerListing listing, uint? lowest, uint floor, string reason, bool belowCost = false) =>
        new(kind, listing.CurrentPrice, null, lowest, floor, reason, belowCost);

    private static bool RelaxationDue(PricingContext context, StuckStockPolicy stuck) =>
        context.BelowCostSince is { } since && context.Now is { } now && now - since >= stuck.RelaxAfter;

    private static double HoursBelowCost(PricingContext context) =>
        context.BelowCostSince is { } since && context.Now is { } now ? (now - since).TotalHours : 0;

    /// <summary>
    /// A listing cheaper than it needs to be moves up to the front of the queue: one
    /// undercut under the cheapest competitor itself, not the depth-adjusted price,
    /// which can sit behind cheaper rows and would give up first place. Below cost
    /// the goal is the floor instead, so stock is not left listed at a loss.
    /// </summary>
    private static PriceDecision RaiseTowardFront(PricingContext context, IReadOnlyList<MarketListing> competitors,
        uint floor, uint desired, bool belowCost, PriceRaisePolicy raise)
    {
        var rule = context.Rule;
        var cheapest = competitors.Min(x => x.PricePerUnit);
        var front = rule.Mode == PricingMode.MatchLowest
            ? cheapest
            : cheapest > rule.UndercutAmount ? cheapest - rule.UndercutAmount : 1u;
        var goal = belowCost ? desired : Math.Max(floor, RoundDown(front, rule.Rounding));
        return RaiseTo(context, goal, cheapest, floor, raise, belowCost) ??
               Decision(PriceDecisionKind.NoChange, context.Listing, cheapest, floor,
                   "Current listing is already at the front of the queue.", belowCost);
    }

    /// <summary>
    /// The only competition is our own stock, so nothing marks a front to sit behind
    /// and the recent median sets the price. A board that came back empty proves
    /// nothing, and a row count on a page boundary may be a read that stopped short
    /// of a dearer competitor, so either keeps the listing where it is.
    /// </summary>
    private static PriceDecision? RaiseAsSoleSeller(PricingContext context)
    {
        if (context.Raise is not { } raise)
            return null;
        var count = context.Market.Listings.Count;
        if (count == 0 || count % 10 == 0 || context.Market.HistoricalMedianFor(context.Listing.IsHighQuality) is not > 0)
            return null;
        var floor = PositionCostPolicy.MinimumListingPrice(context.Rule, context.Fees, context.Listing.AcquisitionCost);
        return RaiseTo(context, MaximumListingPrice, null, floor, raise, false);
    }

    /// <summary>
    /// Raise to <paramref name="goal"/>, capped at a premium over what this quality
    /// recently sold for. Null when no raise is due: no same-quality history to bound
    /// it, or a step too small to be worth a price write.
    /// </summary>
    private static PriceDecision? RaiseTo(PricingContext context, uint goal, uint? lowest, uint floor,
        PriceRaisePolicy raise, bool belowCost)
    {
        var listing = context.Listing;
        if (goal <= listing.CurrentPrice)
            return null;
        if (context.Market.HistoricalMedianFor(listing.IsHighQuality) is not (> 0 and var median))
            return Decision(PriceDecisionKind.NoChange, listing, lowest, floor,
                $"Priced under the market, but no {(listing.IsHighQuality ? "HQ" : "NQ")} sale history arrived to " +
                "bound a raise; holding.", belowCost);
        var cap = (uint)Math.Min(MaximumListingPrice, decimal.Floor(median * raise.MedianCapPercent / 100m));
        var target = RoundDown(Math.Min(goal, cap), context.Rule.Rounding);
        if (target <= listing.CurrentPrice ||
            (target - listing.CurrentPrice) * 100m < listing.CurrentPrice * raise.MinimumRaisePercent)
            return null;
        var quality = listing.IsHighQuality ? "HQ" : "NQ";
        return new PriceDecision(PriceDecisionKind.Update, listing.CurrentPrice, target, lowest, floor,
            !lowest.HasValue
                ? $"No other {quality} seller; raised to {raise.MedianCapPercent:N0}% of the recent median sale ({median:N0})."
                : goal > cap
                    ? $"Raised toward the cheapest competitor, capped at {raise.MedianCapPercent:N0}% of the recent " +
                      $"{quality} median sale ({median:N0})."
                    : "Raised to just under the cheapest competitor instead of selling cheaper than needed.",
            belowCost);
    }

    /// <summary>
    /// The cheapest price with more competing inventory in front of it than the
    /// market absorbs in the sampling window. Degrades to the cheapest listing when
    /// no absorption allowance is supplied.
    /// </summary>
    private static uint DepthAdjustedLowest(IReadOnlyList<MarketListing> competitors, decimal absorbableUnits)
    {
        var ordered = competitors.OrderBy(x => x.PricePerUnit).ToArray();
        if (ordered.Length == 0)
            return 0;
        var absorbable = absorbableUnits <= 0 ? 0m : decimal.Floor(absorbableUnits);
        if (absorbable <= 0)
            return ordered[0].PricePerUnit;
        decimal cumulative = 0;
        foreach (var listing in ordered)
        {
            cumulative += listing.Quantity;
            if (cumulative > absorbable)
                return listing.PricePerUnit;
        }
        return ordered[^1].PricePerUnit;
    }

    private static bool IsPriceWar(uint lowest, uint? historicalMedian, decimal dropPercent)
    {
        if (!historicalMedian.HasValue || historicalMedian.Value == 0 || dropPercent <= 0)
            return false;

        var threshold = historicalMedian.Value * (1m - (dropPercent / 100m));
        return lowest < threshold;
    }

    private static uint ProtectedHistoricalFloor(uint historicalMedian, decimal dropPercent) =>
        (uint)Math.Max(1, decimal.Floor(historicalMedian * (1m - (dropPercent / 100m))));

    private static bool WithinTolerance(uint current, uint lowest, PricingRule rule)
    {
        var difference = Math.Abs((long)current - lowest);
        var absoluteMatch = rule.AbsoluteTolerance > 0 && difference <= rule.AbsoluteTolerance;
        var percent = lowest == 0 ? decimal.MaxValue : difference * 100m / lowest;
        var percentageMatch = rule.PercentageTolerance > 0 && percent <= rule.PercentageTolerance;
        return absoluteMatch || percentageMatch;
    }

    private static bool IsQualityAllowed(bool marketHq, bool listingHq, QualityFilterMode mode) => mode switch
    {
        QualityFilterMode.SameQuality => marketHq == listingHq,
        QualityFilterMode.AllQualities => true,
        QualityFilterMode.HighQualityOnly => marketHq,
        QualityFilterMode.NormalQualityOnly => !marketHq,
        _ => false,
    };

    private static string QualityDescription(QualityFilterMode mode, bool listingHq) => mode switch
    {
        QualityFilterMode.SameQuality => listingHq ? "the HQ market" : "the NQ market",
        QualityFilterMode.AllQualities => "the combined HQ/NQ market",
        QualityFilterMode.HighQualityOnly => "the HQ market",
        QualityFilterMode.NormalQualityOnly => "the NQ market",
        _ => "the selected quality market",
    };

    private static uint RoundDown(uint value, PriceRoundingMode mode) => mode switch
    {
        PriceRoundingMode.None => value,
        PriceRoundingMode.EndIn99 => RoundDownToEnding(value, 100, 99),
        PriceRoundingMode.EndIn999 => RoundDownToEnding(value, 1000, 999),
        _ => value,
    };

    private static uint RoundDownToEnding(uint value, uint modulus, uint ending)
    {
        if (value <= ending)
            return value;

        var candidate = (value / modulus * modulus) + ending;
        return candidate <= value ? candidate : candidate - modulus;
    }
}
