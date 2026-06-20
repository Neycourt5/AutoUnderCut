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
    IReadOnlyList<RetainerPortfolioSummary> Retainers);
