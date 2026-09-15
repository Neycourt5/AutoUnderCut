namespace SmartUndercutBot.Core.Models;

public sealed record PortfolioListingEstimate(
    ulong RetainerId,
    string RetainerName,
    short Slot,
    uint ItemId,
    string ItemName,
    uint Quantity,
    uint AskingUnitPrice,
    uint EstimatedUnitPrice,
    bool HasLiveMarketEstimate,
    decimal SellerFeePercent,
    bool IsHighQuality = false);

/// <summary>Marketable stock physically present in the player's bags.</summary>
public sealed record PortfolioBagHolding(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity);

public sealed record PortfolioBagPrice(
    uint ItemId,
    bool IsHighQuality,
    uint? HomeMarketUnitPrice,
    uint? TrackedUnitCost);

public enum BagValuationSource
{
    Unknown,
    HomeMarket,
    PurchaseCost,
}

public sealed record PortfolioBagStockEstimate(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint EstimatedUnitPrice,
    BagValuationSource Source,
    decimal SellerFeePercent);

public sealed record PortfolioRetainerBalance(
    ulong RetainerId,
    string RetainerName,
    uint Gil,
    decimal SellerFeePercent);

public sealed record RetainerPortfolioSummary(
    ulong RetainerId,
    string RetainerName,
    int Listings,
    ulong Units,
    ulong GrossAskingValue,
    ulong EstimatedGrossValue,
    ulong EstimatedNetAtAsking,
    ulong EstimatedNetMarketAligned,
    uint RetainerGil,
    decimal SellerFeePercent);

public sealed record PortfolioValuation(
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsFullBellRun,
    bool IsComplete,
    int RetainersScanned,
    int ExpectedRetainers,
    int Listings,
    ulong Units,
    ulong GrossAskingValue,
    ulong EstimatedGrossValue,
    ulong EstimatedNetAtAsking,
    ulong EstimatedNetMarketAligned,
    ulong EstimatedMarkdown,
    uint PlayerGil,
    ulong RetainerGil,
    ulong CurrentGil,
    ulong ProjectedWealthAtAsking,
    ulong ProjectedWealthMarketAligned,
    int LiveEstimatedListings,
    IReadOnlyList<RetainerPortfolioSummary> Retainers,
    int BagItemTypes = 0,
    ulong BagUnits = 0,
    ulong EstimatedBagGrossValue = 0,
    ulong EstimatedBagNetValue = 0,
    int UnknownBagItemTypes = 0,
    ulong UnknownBagUnits = 0,
    int CostBasisBagItemTypes = 0);
