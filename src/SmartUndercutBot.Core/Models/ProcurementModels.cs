using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Core.Models;

/// <summary>
/// Where a holding or a prospective purchase sits in the trading portfolio.
/// Core is the stock the player actually wants to trade; Opportunistic is the
/// arbitrage that must never be allowed to crowd out the rest.
/// </summary>
public enum PortfolioTier
{
    Core,
    Secondary,
    Opportunistic,
}

/// <summary>
/// Portfolio shape and the value gate. The secondary liquidity floors live in
/// <see cref="PortfolioPolicy"/> as constants; only these three are configurable.
/// </summary>
public sealed record PortfolioGates(
    decimal PreferredTargetPercent = 75m,
    decimal OpportunisticMaximumPercent = 10m,
    uint MinimumProfitPerSaleSlot = 0)
{
    /// <summary>The shipped portfolio shape. Configuration supplies the live values.</summary>
    public static PortfolioGates Default { get; } = new();

    /// <summary>
    /// No portfolio shaping at all, used when a caller supplies no gates. Tier
    /// ordering still applies, but nothing is capped or rejected on value, so a
    /// caller that has not opted in keeps plain profitability behaviour.
    /// </summary>
    public static PortfolioGates Unrestricted { get; } = new(0m, 100m, 0);
}

public sealed record PortfolioAllocationSummary(
    int CoreSlots,
    int CoreTarget,
    int SecondarySlots,
    int OpportunisticSlots,
    int OpportunisticCap,
    int CapacitySlots)
{
    public static PortfolioAllocationSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);
    public int CoreDeficit => Math.Max(0, CoreTarget - CoreSlots);
    public int OpportunisticHeadroom => Math.Max(0, OpportunisticCap - OpportunisticSlots);
    public override string ToString() =>
        $"Preferred {CoreSlots}/{CoreTarget} target | Secondary {SecondarySlots} | " +
        $"Opportunistic {OpportunisticSlots}/{OpportunisticCap} cap";
}

/// <summary>One line of "why was this bought or skipped", for the log and the UI.</summary>
public sealed record PortfolioDecision(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    PortfolioTier Tier,
    bool Selected,
    string Reason,
    decimal SalesPerDay,
    uint ExpectedProfit,
    decimal RoiPercent,
    decimal DaysToSell,
    ulong ResaleValue,
    ulong LandedCost = 0,
    ulong ExpectedNetProceeds = 0,
    decimal ExpectedGilPerDay = 0m,
    decimal CoverageDaysBefore = 0m,
    decimal CoverageDaysAfter = 0m,
    uint Quantity = 0,
    // What the allocator actually ranked on: gil per day of committed capital given
    // the inventory queued ahead of this stack, weighted by the market's class.
    decimal MarginalGilPerDay = 0m,
    decimal MarginalDaysToClear = 0m,
    decimal AllocationScore = 0m)
{
    /// <summary>
    /// The decision line as it appears in the log. It has to answer "why did this
    /// win?" on its own, so it carries the money, the margin, the demand and how
    /// much inventory the purchase creates.
    /// </summary>
    public override string ToString()
    {
        var head = $"{(Selected ? "BUY" : "SKIP")} {ItemName}{(IsHighQuality ? " HQ" : string.Empty)}" +
                   $"{(Quantity > 0 ? $" x{Quantity}" : string.Empty)} [{Tier.ToString().ToUpperInvariant()}]";
        var money = LandedCost > 0
            ? $"cost {LandedCost:N0}, sale {ExpectedNetProceeds:N0} net, profit {ExpectedProfit:N0} " +
              $"({RoiPercent:N1}% ROI)"
            : $"profit {ExpectedProfit:N0} ({RoiPercent:N1}% ROI), resale value {ResaleValue:N0}";
        var market = $"{SalesPerDay:N0}/day, turnover {DaysToSell:N2}d, {ExpectedGilPerDay:N0} gil/day";
        var coverage = CoverageDaysAfter > 0
            ? $", coverage {CoverageDaysBefore:N1}d -> {CoverageDaysAfter:N1}d"
            : string.Empty;
        // Only worth printing once inventory is queued ahead of the stack, where the
        // marginal figure and the standalone one stop agreeing.
        var marginal = MarginalDaysToClear > 0 && MarginalGilPerDay != ExpectedGilPerDay
            ? $"; marginal {MarginalGilPerDay:N0} gil/day over {MarginalDaysToClear:N2}d" +
              (AllocationScore > 0 ? $", score {AllocationScore:N0}" : string.Empty)
            : AllocationScore > 0 ? $"; score {AllocationScore:N0}" : string.Empty;
        return $"{head}: {money}; {market}{coverage}{marginal}; {Reason}";
    }
}

/// <summary>
/// How much a market has proved about itself. Discovery proposes at the bottom of
/// this ladder; only sustained, agreeing evidence reaches the top, where an item is
/// pinned as core stock.
/// </summary>
public enum MarketConfidence
{
    /// <summary>Worth a side profit at most.</summary>
    Opportunistic,
    /// <summary>Looks like real trading stock, but has not been observed long enough.</summary>
    Candidate,
    /// <summary>Repeatedly demonstrated core-stock volume, value, stability and margin.</summary>
    Proven,
}

public sealed class ProcurementRule
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AllowHighQuality { get; set; }
    public uint MaximumUnitPrice { get; set; }
    public int TargetStackSize { get; set; } = 99;
    public int MaximumSaleSlots { get; set; } = 8;
    public int MinimumWeeklyUnitsSold { get; set; } = 20;
    public bool RequireHighQuality { get; set; }
    public bool? ListFromBags { get; set; }
    public uint? BagReserveQuantity { get; set; }
    // Sell what is already held, never buy more. Used for stock the player wants
    // cleared out rather than traded.
    public bool LiquidateOnly { get; set; }
    // Pinned core portfolio stock: the high-value, high-volume consumables that
    // should occupy most of the retainer slots. Never demoted by metrics.
    public bool PreferredStock { get; set; }
    // Set when automatic market discovery proposed this rule, so a later discovery
    // pass may refresh or retire it without touching anything edited by hand.
    public bool DiscoveredAutomatically { get; set; }
    // How much a discovered market has proved about itself, and the evidence behind
    // it. A single flattering statistics call reaches Candidate and no further;
    // PreferredStock is only set once the line has agreed with itself repeatedly.
    public MarketConfidence Confidence { get; set; } = MarketConfidence.Candidate;
    public int DiscoveryConfirmations { get; set; }
    public uint LastObservedUnitPrice { get; set; }
    public decimal LastObservedSalesPerDay { get; set; }
    // The all-world tour is slow - every extra item is multiplied by the number of
    // worlds visited - so only stock explicitly marked for it is walked, in order.
    public bool HuntOnTour { get; set; }
    public int TourPriority { get; set; } = 5;
    // Priced on every world of the circuit rather than waiting for the rotation to
    // reach it. For the lines worth sniping: the curated consumables, and the rare
    // dyes whose away-world listings are occasionally far below the home price.
    // Unlike PreferredStock this says nothing about portfolio tiering or budget.
    public bool AlwaysScout { get; set; }
    public ProcurementRule Clone() => (ProcurementRule)MemberwiseClone();
}

public sealed record StockExposure(
    uint ItemId,
    bool IsHighQuality,
    uint Quantity,
    int SaleSlots,
    PortfolioTier Tier = PortfolioTier.Opportunistic);

public sealed record ProcurementMarketListing(
    uint ItemId,
    ulong ListingId,
    ulong RetainerId,
    string WorldName,
    uint WorldId,
    uint PricePerUnit,
    uint Quantity,
    bool IsHighQuality,
    string SourceListingId = "");

/// <summary>
/// A cached or aggregated price observation. Deliberately not a
/// <see cref="ProcurementMarketListing"/>: hints choose where to look, and cannot
/// be mistaken for a listing the planner is allowed to buy.
/// </summary>
public sealed record MarketPriceHint(
    uint ItemId,
    string WorldName,
    uint WorldId,
    uint PricePerUnit,
    bool IsHighQuality,
    decimal SalesPerDay = 0m);

public sealed record ProcurementSale(uint PricePerUnit, uint Quantity, bool IsHighQuality, DateTimeOffset SoldAt);

public sealed record ProcurementMarketItem(
    uint ItemId,
    string ItemName,
    IReadOnlyList<ProcurementMarketListing> Listings,
    IReadOnlyList<ProcurementSale> RecentSales,
    decimal? NqSalesPerDay = null,
    decimal? HqSalesPerDay = null);

public sealed record ProcurementPlanRequest(
    IReadOnlyList<ProcurementMarketItem> Markets,
    IReadOnlyList<ProcurementRule> Rules,
    uint GilBudget,
    int FreeSaleSlots,
    int FreeInventorySlots,
    decimal MinimumRoiPercent,
    uint MinimumProfitPerUnit,
    decimal MarketTaxPercent = 5m,
    decimal BuyerFeePercent = 5m,
    string HomeWorld = "",
    IReadOnlySet<ulong>? OwnedRetainerIds = null,
    IReadOnlyList<StockExposure>? OwnedStock = null,
    decimal MaximumWeeklySalesSharePercent = 100m,
    bool HighQualityOnly = false,
    IReadOnlyList<ProcurementMarketListing>? ResaleListings = null,
    PortfolioGates? Portfolio = null,
    int PortfolioCapacitySlots = 0,
    // Coverage targets, margin bars by class of stock, and scoring weights. Null
    // keeps the flat MinimumRoiPercent bar and no coverage shaping, so a caller
    // that has not opted in gets plain profitability behaviour.
    ProcurementEconomicPolicy? Economics = null,
    // Sub-limits within GilBudget/FreeSaleSlots. PreferredStock rules alone can
    // use the remainder; all purchases still consume the total budget and slots.
    uint? NonPreferredGilBudget = null,
    int? NonPreferredSaleSlots = null);

public sealed record LiveMarketPlanRequest(
    IReadOnlyList<ProcurementMarketItem> Markets,
    IReadOnlyList<ProcurementRule> Rules,
    string HomeWorld,
    IReadOnlySet<ulong> OwnedRetainerIds,
    uint GilBudget,
    int FreeSaleSlots,
    int FreeInventorySlots,
    decimal MinimumRoiPercent,
    uint MinimumProfitPerUnit,
    decimal MarketTaxPercent = 5m,
    decimal BuyerFeePercent = 5m,
    IReadOnlyList<StockExposure>? OwnedStock = null,
    bool HighQualityOnly = false,
    PortfolioGates? Portfolio = null,
    int PortfolioCapacitySlots = 0,
    ProcurementEconomicPolicy? Economics = null,
    uint? NonPreferredGilBudget = null,
    int? NonPreferredSaleSlots = null);

public sealed record ProcurementOrder(
    uint ItemId,
    string ItemName,
    ulong ListingId,
    ulong RetainerId,
    string WorldName,
    uint WorldId,
    uint PricePerUnit,
    uint Quantity,
    bool IsHighQuality,
    uint TargetSalePrice,
    uint MaximumAcceptableUnitPrice,
    uint ExpectedProfit,
    int SaleSlots,
    decimal SalesPerDay = 0m,
    bool IsFillOrder = false,
    PortfolioTier Tier = PortfolioTier.Opportunistic,
    // Purchase price plus the buyer fee: the gil actually put at risk. Supplied by
    // the planner from the canonical fee model so every consumer reports and
    // enforces the same ROI. Zero means "not costed", and the raw price stands in.
    ulong LandedCost = 0,
    // Units of this item already held or listed when the plan was built, and the
    // sizing target it was measured against. Carried for logging and for the live
    // re-check, so a decision can be explained without recomputing the market.
    ulong OwnedUnitsBefore = 0,
    ulong TargetUnits = 0,
    decimal AllocationScore = 0m,
    decimal RequiredRoiPercent = 0m)
{
    private int Slots => Math.Max(1, SaleSlots);

    /// <summary>Gil actually at risk in this purchase, fee included.</summary>
    public ulong CapitalAtRisk => LandedCost > 0 ? LandedCost : FeeModel.Default.LandedCost(PricePerUnit, Quantity);

    /// <summary>Gil the stack is expected to return at the resale anchor, before fees.</summary>
    public ulong ExpectedResaleValue => (ulong)TargetSalePrice * Quantity;

    /// <summary>Proceeds after the sale tax. Profit is defined as this minus the landed cost.</summary>
    public ulong ExpectedNetProceeds => CapitalAtRisk + ExpectedProfit;

    public ulong ResaleValuePerSaleSlot => ExpectedResaleValue / (ulong)Slots;
    public ulong ExpectedProfitPerSaleSlot => ExpectedProfit / (ulong)Slots;
    public decimal EstimatedDaysToSell => PortfolioPolicy.DaysToSell(Quantity, SalesPerDay);

    /// <summary>
    /// Expected gil generated per day of sale-slot occupancy. The primary measure of
    /// how hard this purchase makes the capital and the slot work.
    /// </summary>
    public decimal ExpectedGilPerDay =>
        PortfolioPolicy.ExpectedGilPerDay(ExpectedProfit, EstimatedDaysToSell) / Slots;

    /// <summary>
    /// Days until this stack has finished selling, counting the units already held
    /// or planned that sit in front of it in our own queue. Equal to
    /// <see cref="EstimatedDaysToSell"/> when the position is empty.
    /// </summary>
    public decimal MarginalDaysToClear =>
        PortfolioPolicy.MarginalDaysToClear(OwnedUnitsBefore, Quantity, SalesPerDay);

    /// <summary>
    /// Gil per day of committed capital for this stack given the inventory in front
    /// of it - the figure the allocator ranks on. The standalone
    /// <see cref="ExpectedGilPerDay"/> is what the stack would earn on an empty
    /// position; this is what it earns where it will actually sit.
    /// </summary>
    public decimal MarginalGilPerDay =>
        PortfolioPolicy.MarginalGilPerDay(ExpectedProfit, OwnedUnitsBefore, Quantity, SalesPerDay) / Slots;

    /// <summary>Days of this market's demand already held before the purchase.</summary>
    public decimal InventoryCoverageDays =>
        InventoryCoveragePolicy.CoverageDays(OwnedUnitsBefore, SalesPerDay);

    /// <summary>Days of demand held once this stack lands.</summary>
    public decimal CoverageDaysAfterPurchase =>
        InventoryCoveragePolicy.CoverageDays(OwnedUnitsBefore + Quantity, SalesPerDay);

    /// <summary>
    /// The one ROI definition in the codebase: profit over the gil actually put at
    /// risk, buyer fee included. Displayed, enforced and re-checked identically.
    /// </summary>
    public decimal RoiPercent => NetRoiPercent;
    public decimal NetRoiPercent => FeeModel.NetRoiPercent(ExpectedNetProceeds, CapitalAtRisk);
}

public sealed record ProcurementPlan(
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProcurementOrder> Orders,
    uint TotalCost,
    uint ExpectedProfit,
    int SaleSlots,
    PortfolioAllocationSummary? Portfolio = null,
    IReadOnlyList<PortfolioDecision>? Decisions = null)
{
    public static ProcurementPlan Empty { get; } = new(DateTimeOffset.UtcNow, [], 0, 0, 0);
    public PortfolioAllocationSummary Summary => Portfolio ?? PortfolioAllocationSummary.Empty;
    public IReadOnlyList<PortfolioDecision> DecisionLog => Decisions ?? [];
}
