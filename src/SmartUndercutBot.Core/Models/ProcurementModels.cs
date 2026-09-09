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
    ulong ResaleValue)
{
    public override string ToString() =>
        $"{ItemName}{(IsHighQuality ? " HQ" : string.Empty)} {Tier.ToString().ToUpperInvariant()} " +
        $"{SalesPerDay:N0} units/day, {ExpectedProfit:N0} expected profit, {RoiPercent:N0}% ROI, " +
        $"estimated turnover {DaysToSell:N2} days; {(Selected ? "selected" : "skipped")}: {Reason}";
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
    // The all-world tour is slow - every extra item is multiplied by the number of
    // worlds visited - so only stock explicitly marked for it is walked, in order.
    public bool HuntOnTour { get; set; }
    public int TourPriority { get; set; } = 5;
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
    int PortfolioCapacitySlots = 0);

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
    int PortfolioCapacitySlots = 0);

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
    PortfolioTier Tier = PortfolioTier.Opportunistic)
{
    private int Slots => Math.Max(1, SaleSlots);

    /// <summary>Gil the stack is expected to return at the resale anchor, before fees.</summary>
    public ulong ExpectedResaleValue => (ulong)TargetSalePrice * Quantity;
    public ulong ResaleValuePerSaleSlot => ExpectedResaleValue / (ulong)Slots;
    public ulong ExpectedProfitPerSaleSlot => ExpectedProfit / (ulong)Slots;
    public decimal EstimatedDaysToSell => PortfolioPolicy.DaysToSell(Quantity, SalesPerDay);

    /// <summary>Expected profit per day of sale-slot occupancy.</summary>
    public decimal ProfitVelocity => PortfolioPolicy.ProfitVelocity(ExpectedProfit, EstimatedDaysToSell) / Slots;

    /// <summary>ROI on the landed cost, kept as a safety guard rather than the objective.</summary>
    public decimal RoiPercent
    {
        get
        {
            var cost = (decimal)PricePerUnit * Quantity;
            return cost <= 0 ? 0 : ExpectedProfit / cost * 100m;
        }
    }
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
