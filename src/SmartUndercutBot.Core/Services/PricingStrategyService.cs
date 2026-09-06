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
            rule.PercentageTolerance < 0)
        {
            return Decision(PriceDecisionKind.InvalidData, listing, null, 0, "Listing or rule data is outside valid bounds.");
        }

        var floor = CalculateFloor(listing, rule);
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
            return Decision(PriceDecisionKind.NoMarketData, listing, null, floor,
                $"No competitor listings matched {QualityDescription(rule.QualityFilter, listing.IsHighQuality)}.");

        var lowest = competitors.Min(x => x.PricePerUnit);
        if (IsPriceWar(lowest, market.HistoricalMedianPrice, rule.PriceWarDropPercent))
        {
            if (rule.PriceWarAction == PriceWarAction.LeaveUnchanged)
                return Decision(PriceDecisionKind.PriceWar, listing, lowest, floor, "Lowest price breached the configured historical drop threshold.");

            var protectedFloor = Math.Max(floor, ProtectedHistoricalFloor(market.HistoricalMedianPrice!.Value, rule.PriceWarDropPercent));
            if (listing.CurrentPrice == protectedFloor)
                return Decision(PriceDecisionKind.NoChange, listing, lowest, protectedFloor, "Listing already matches the protected historical floor.");

            return new PriceDecision(PriceDecisionKind.Update, listing.CurrentPrice, protectedFloor, lowest, protectedFloor,
                "Price war detected; using the protected historical floor.");
        }

        if (listing.CurrentPrice <= lowest)
            return Decision(PriceDecisionKind.NoChange, listing, lowest, floor,
                $"Current listing is already at or below the lowest competitor in {QualityDescription(rule.QualityFilter, listing.IsHighQuality)}.");

        if (WithinTolerance(listing.CurrentPrice, lowest, rule))
            return Decision(PriceDecisionKind.WithinTolerance, listing, lowest, floor, "Current price is inside the configured tolerance band.");

        var rawTarget = rule.Mode == PricingMode.MatchLowest
            ? lowest
            : lowest > rule.UndercutAmount ? lowest - rule.UndercutAmount : 1u;

        if (rawTarget < floor)
            return Decision(PriceDecisionKind.BelowFloor, listing, lowest, floor, "Competitive target would fall below the effective minimum price.");

        var rounded = RoundDown(rawTarget, rule.Rounding);
        var target = Math.Max(floor, rounded);
        if (target > MaximumListingPrice)
            return Decision(PriceDecisionKind.InvalidData, listing, lowest, floor, "Calculated target exceeds the game's listing-price limit.");

        if (target == listing.CurrentPrice)
            return Decision(PriceDecisionKind.NoChange, listing, lowest, floor, "Calculated target equals the current price.");

        return new PriceDecision(PriceDecisionKind.Update, listing.CurrentPrice, target, lowest, floor,
            $"{rule.Mode} target calculated from the lowest valid competitor price.");
    }

    private static PriceDecision Decision(PriceDecisionKind kind, RetainerListing listing, uint? lowest, uint floor, string reason) =>
        new(kind, listing.CurrentPrice, null, lowest, floor, reason);

    private static uint CalculateFloor(RetainerListing listing, PricingRule rule)
    {
        var costBasis = listing.AcquisitionCost == 0 ? rule.CostBasis : listing.AcquisitionCost;
        if (costBasis == 0)
            return Math.Max(1, rule.MinimumPrice);

        var marginFloor = decimal.Ceiling(costBasis * (1m + (rule.MinimumMarginPercent / 100m)));
        return Math.Max(rule.MinimumPrice, (uint)Math.Min(marginFloor, MaximumListingPrice));
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
