namespace SmartUndercutBot.Core.Models;

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
}

public sealed record StockExposure(uint ItemId, bool IsHighQuality, uint Quantity, int SaleSlots);

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

public sealed record ProcurementSale(uint PricePerUnit, uint Quantity, bool IsHighQuality, DateTimeOffset SoldAt);

public sealed record ProcurementMarketItem(
    uint ItemId,
    string ItemName,
    IReadOnlyList<ProcurementMarketListing> Listings,
    IReadOnlyList<ProcurementSale> RecentSales);

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
    bool HighQualityOnly = false);

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
    bool HighQualityOnly = false);

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
    int SaleSlots);

public sealed record ProcurementPlan(
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProcurementOrder> Orders,
    uint TotalCost,
    uint ExpectedProfit,
    int SaleSlots)
{
    public static ProcurementPlan Empty { get; } = new(DateTimeOffset.UtcNow, [], 0, 0, 0);
}
