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
    bool IsFromCache = false);

public sealed class PricingRule
{
    public PricingMode Mode { get; set; } = PricingMode.Undercut;
    public uint UndercutAmount { get; set; } = 1;
    public uint MinimumPrice { get; set; } = 1;
    public uint CostBasis { get; set; }
    public decimal MinimumMarginPercent { get; set; }
    public uint AbsoluteTolerance { get; set; }
    public decimal PercentageTolerance { get; set; }
    public decimal PriceWarDropPercent { get; set; } = 20m;
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
