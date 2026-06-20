using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public interface IPortfolioValuationService
{
    PortfolioValuation Calculate(
        IReadOnlyCollection<PortfolioListingEstimate> listings,
        IReadOnlyCollection<PortfolioRetainerBalance> retainers,
        uint playerGil,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isFullBellRun,
        bool isComplete,
        int expectedRetainers);
}

public sealed class PortfolioValuationService : IPortfolioValuationService
{
    public PortfolioValuation Calculate(
        IReadOnlyCollection<PortfolioListingEstimate> listings,
        IReadOnlyCollection<PortfolioRetainerBalance> retainers,
        uint playerGil,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isFullBellRun,
        bool isComplete,
        int expectedRetainers)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(retainers);

        var balances = retainers
            .GroupBy(x => x.RetainerId)
            .ToDictionary(x => x.Key, x => x.Last());
        var summaries = listings
            .GroupBy(x => x.RetainerId)
            .Select(group =>
            {
                balances.TryGetValue(group.Key, out var balance);
                var rows = group.ToArray();
                var asking = SumGross(rows, x => x.AskingUnitPrice);
                var market = SumGross(rows, x => x.EstimatedUnitPrice);
                return new RetainerPortfolioSummary(
                    group.Key,
                    balance?.RetainerName ?? rows[0].RetainerName,
                    rows.Length,
                    rows.Aggregate<PortfolioListingEstimate, ulong>(0, (sum, x) => sum + x.Quantity),
                    asking,
                    market,
                    SumNet(rows, x => x.AskingUnitPrice),
                    SumNet(rows, x => x.EstimatedUnitPrice),
                    balance?.Gil ?? 0,
                    balance?.SellerFeePercent ?? rows[0].SellerFeePercent);
            })
            .Concat(balances.Values
                .Where(balance => listings.All(x => x.RetainerId != balance.RetainerId))
                .Select(balance => new RetainerPortfolioSummary(
                    balance.RetainerId, balance.RetainerName, 0, 0, 0, 0, 0, 0,
                    balance.Gil, balance.SellerFeePercent)))
            .OrderBy(x => x.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var grossAsking = summaries.Aggregate<RetainerPortfolioSummary, ulong>(0, (sum, x) => sum + x.GrossAskingValue);
        var grossMarket = summaries.Aggregate<RetainerPortfolioSummary, ulong>(0, (sum, x) => sum + x.EstimatedGrossValue);
        var netAsking = summaries.Aggregate<RetainerPortfolioSummary, ulong>(0, (sum, x) => sum + x.EstimatedNetAtAsking);
        var netMarket = summaries.Aggregate<RetainerPortfolioSummary, ulong>(0, (sum, x) => sum + x.EstimatedNetMarketAligned);
        var retainerGil = summaries.Aggregate<RetainerPortfolioSummary, ulong>(0, (sum, x) => sum + x.RetainerGil);
        var currentGil = (ulong)playerGil + retainerGil;

        return new PortfolioValuation(
            startedAt,
            completedAt,
            isFullBellRun,
            isComplete,
            summaries.Length,
            Math.Max(0, expectedRetainers),
            listings.Count,
            listings.Aggregate<PortfolioListingEstimate, ulong>(0, (sum, x) => sum + x.Quantity),
            grossAsking,
            grossMarket,
            netAsking,
            netMarket,
            grossAsking > grossMarket ? grossAsking - grossMarket : 0,
            playerGil,
            retainerGil,
            currentGil,
            currentGil + netAsking,
            currentGil + netMarket,
            listings.Count(x => x.HasLiveMarketEstimate),
            summaries);
    }

    public static ulong NetAfterSellerFee(ulong gross, decimal sellerFeePercent)
    {
        var rate = Math.Clamp(sellerFeePercent, 0m, 100m) / 100m;
        var fee = (ulong)decimal.Floor(gross * rate);
        return gross - fee;
    }

    private static ulong SumGross(
        IEnumerable<PortfolioListingEstimate> listings,
        Func<PortfolioListingEstimate, uint> unitPrice) =>
        listings.Aggregate<PortfolioListingEstimate, ulong>(0,
            (sum, x) => sum + ((ulong)unitPrice(x) * x.Quantity));

    private static ulong SumNet(
        IEnumerable<PortfolioListingEstimate> listings,
        Func<PortfolioListingEstimate, uint> unitPrice) =>
        listings.Aggregate<PortfolioListingEstimate, ulong>(0, (sum, x) =>
            sum + NetAfterSellerFee((ulong)unitPrice(x) * x.Quantity, x.SellerFeePercent));
}
