using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Core.Models;

public enum PricingMode
{
    Undercut,
    MatchLowest,
}

public enum PriceRoundingMode
{
    None,
    EndIn99,
    EndIn999,
}

public enum QualityFilterMode
{
    SameQuality,
    AllQualities,
    HighQualityOnly,
    NormalQualityOnly,
}

public enum PriceWarAction
{
    LeaveUnchanged,
    MatchProtectedFloor,
}

public enum PriceDecisionKind
{
    Update,
    NoChange,
    WithinTolerance,
    PriceWar,
    BelowFloor,
    NoMarketData,
    InvalidData,
}

public sealed record RetainerListing(
    ulong RetainerId,
    string RetainerName,
    short Slot,
    uint ItemId,
    string ItemName,
    uint Quantity,
    uint CurrentPrice,
    bool IsHighQuality,
    uint AcquisitionCost = 0);

// ListingId keeps two identical stacks from one retainer distinct. Without it,
// value equality merged them, so a snapshot undercounted the supply in front of a
// price and the resale anchor walked straight past it.
public sealed record MarketListing(
    uint PricePerUnit,
    uint Quantity,
    bool IsHighQuality,
    string? RetainerName = null,
    ulong RetainerId = 0,
    ulong ListingId = 0);

public sealed record MarketSnapshot(
    uint ItemId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<MarketListing> Listings,
    uint? HistoricalMedianPrice,
    bool IsFromCache = false,
    // Competing units the market absorbs before our own stack is realistically
    // reached. Zero keeps the plain "cheapest listing wins" behaviour; a positive
    // value lets a big stack ignore a trivial undercut it would outlive anyway.
    decimal AbsorbableUnits = 0m,
    // The same sale history split by quality. HistoricalMedianPrice mixes both, so
    // cheap NQ sales would hold an HQ price below what HQ buyers actually pay.
    uint? HistoricalMedianHighQuality = null,
    uint? HistoricalMedianNormalQuality = null)
{
    public uint? HistoricalMedianFor(bool highQuality) =>
        highQuality ? HistoricalMedianHighQuality : HistoricalMedianNormalQuality;
}

/// <summary>
/// What to do when the market sits below what the stock cost. Without this the
/// old ask is held, which on a falling market means it never sells at all.
/// </summary>
public sealed record StuckStockPolicy(
    // How long the market must stay below cost before a loss is accepted.
    TimeSpan RelaxAfter,
    // The deepest loss accepted after that, as a percentage of the landed cost.
    decimal MaximumLossPercent)
{
    public static StuckStockPolicy Default { get; } = new(TimeSpan.FromHours(24), 5m);
}

/// <summary>
/// Raising a listing that is cheaper than it needs to be. The raise only ever
/// goes to just under the cheapest competitor, never past a premium over what
/// the same quality recently sold for.
/// </summary>
public sealed record PriceRaisePolicy(
    decimal MedianCapPercent,
    // Smaller raises are not worth a price write.
    decimal MinimumRaisePercent)
{
    public static PriceRaisePolicy Default { get; } = new(110m, 1m);
}

public sealed class PricingRule
{
    public PricingMode Mode { get; set; } = PricingMode.Undercut;
    public uint UndercutAmount { get; set; } = 1;
    public uint MinimumPrice { get; set; } = 1;
    /// <summary>
    /// Explicitly apply MinimumPrice to purchased stock. Older versions wrote
    /// automatic purchase-margin floors into MinimumPrice, so tracked stock uses
    /// its profitable cost floor until the user opts into an additional minimum.
    /// </summary>
    public bool ApplyMinimumPriceToPurchasedStock { get; set; }
    /// <summary>
    /// Weighted average landed cost of the inventory currently held. Blended on each
    /// purchase against what is still on hand, so selling a position out and re-buying
    /// cheaper lowers the basis instead of stranding the item at an old high price.
    /// </summary>
    public uint CostBasis { get; set; }
    /// <summary>Units backing <see cref="CostBasis"/>, so a new purchase can be weighted against it.</summary>
    public uint CostBasisUnits { get; set; }
    /// <summary>
    /// Resale floor that returns more than the stock cost after sale tax. Kept apart from
    /// <see cref="MinimumPrice"/> so recomputing it from a changed basis never
    /// overwrites a floor the user set by hand.
    /// </summary>
    public uint AcquisitionFloor { get; set; }
    public decimal MinimumMarginPercent { get; set; }
    public uint AbsoluteTolerance { get; set; }
    public decimal PercentageTolerance { get; set; }
    public decimal PriceWarDropPercent { get; set; } = 60m;
    public PriceWarAction PriceWarAction { get; set; } = PriceWarAction.LeaveUnchanged;
    public PriceRoundingMode Rounding { get; set; }
    public QualityFilterMode QualityFilter { get; set; } = QualityFilterMode.SameQuality;

    public PricingRule Clone() => (PricingRule)MemberwiseClone();
}

public sealed record PricingContext(
    RetainerListing Listing,
    MarketSnapshot Market,
    PricingRule Rule,
    IReadOnlySet<ulong>? OwnedRetainerIds = null,
    FeeModel? Fees = null,
    // Null keeps the old behaviour: a listing the market has fallen below is held.
    StuckStockPolicy? StuckStock = null,
    // When this item's market was first seen below its cost, and the time now.
    DateTimeOffset? BelowCostSince = null,
    DateTimeOffset? Now = null,
    // Null keeps the old behaviour: a listing is never raised.
    PriceRaisePolicy? Raise = null);

public sealed record PriceDecision(
    PriceDecisionKind Kind,
    uint CurrentPrice,
    uint? TargetPrice,
    uint? LowestMarketPrice,
    uint EffectiveFloor,
    string Reason,
    // The competitive target was under this stock's cost floor. The caller keeps
    // the "below cost since" clock from this, whatever the decision itself was.
    bool MarketBelowCost = false)
{
    public bool ShouldUpdate => Kind == PriceDecisionKind.Update && TargetPrice.HasValue;
}
