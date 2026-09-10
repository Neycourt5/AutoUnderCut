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

public sealed record MarketListing(
    uint PricePerUnit,
    uint Quantity,
    bool IsHighQuality,
    string? RetainerName = null,
    ulong RetainerId = 0);

public sealed record MarketSnapshot(
    uint ItemId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<MarketListing> Listings,
    uint? HistoricalMedianPrice,
    bool IsFromCache = false,
    // Competing units the market absorbs before our own stack is realistically
    // reached. Zero keeps the plain "cheapest listing wins" behaviour; a positive
    // value lets a big stack ignore a trivial undercut it would outlive anyway.
    decimal AbsorbableUnits = 0m);

public sealed class PricingRule
{
    public PricingMode Mode { get; set; } = PricingMode.Undercut;
    public uint UndercutAmount { get; set; } = 1;
    public uint MinimumPrice { get; set; } = 1;
    /// <summary>
    /// Weighted average landed cost of the inventory currently held. Blended on each
    /// purchase against what is still on hand, so selling a position out and re-buying
    /// cheaper lowers the basis instead of stranding the item at an old high price.
    /// </summary>
    public uint CostBasis { get; set; }
    /// <summary>Units backing <see cref="CostBasis"/>, so a new purchase can be weighted against it.</summary>
    public uint CostBasisUnits { get; set; }
    /// <summary>
    /// Resale floor implied by what the stock actually cost. Kept apart from
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
    IReadOnlySet<ulong>? OwnedRetainerIds = null);

public sealed record PriceDecision(
    PriceDecisionKind Kind,
    uint CurrentPrice,
    uint? TargetPrice,
    uint? LowestMarketPrice,
    uint EffectiveFloor,
    string Reason)
{
    public bool ShouldUpdate => Kind == PriceDecisionKind.Update && TargetPrice.HasValue;
}
