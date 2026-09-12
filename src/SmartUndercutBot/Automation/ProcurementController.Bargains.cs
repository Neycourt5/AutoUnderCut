using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public sealed partial class ProcurementController
{
    // The regional comparison chooses which bargain to visit. Once there, a liquid
    // preferred market can supply a bulk holding, even when its small part-stacks
    // were counted as whole sale slots by the original basket.
    private bool CanSweepBargains(ProcurementOrder order)
    {
        var config = configuration.Current;
        var policy = config.EconomicPolicy;
        return priorityShopping && !stockHuntScanning && config.ContinueShoppingWhenStocked &&
            !order.IsFillOrder && order.Tier == PortfolioTier.Core && IsPreferredStock(order.ItemId) &&
            policy.UsesBulkStockTarget(true, order.SalesPerDay,
                (ulong)order.TargetSalePrice * (uint)PreferredStackSize(order.ItemId)) &&
            (priorityDemand.FirstOrDefault(m => m.ItemId == order.ItemId)?.RecentSales.Count(s =>
                s.IsHighQuality == order.IsHighQuality && s.PricePerUnit > 0 && s.Quantity > 0 &&
                s.SoldAt >= timeProvider.GetUtcNow().AddDays(-7) && s.SoldAt <= timeProvider.GetUtcNow()) ?? 0) >= 3;
    }

    private bool RefreshBargainOrder(ProcurementRule sourceRule)
    {
        var template = currentOrder!;
        var config = configuration.Current;
        var maximumPrice = Math.Min(template.PricePerUnit, template.MaximumAcceptableUnitPrice);
        var rule = sourceRule.Clone();
        rule.MaximumUnitPrice = rule.MaximumUnitPrice > 0
            ? Math.Min(rule.MaximumUnitPrice, maximumPrice) : maximumPrice;
        // A purchase lot is not a permanent retainer listing: 97 lots of 25 can be
        // sold as 25 lots of 99. Bound this holding by units/demand and actual bag
        // space, rather than the normal per-item or replacement-buffer slot cap.
        var demand = priorityDemand.First(m => m.ItemId == template.ItemId);
        var live = market.ReadLiveListings(template.ItemId).Where(x =>
            x.IsHighQuality == template.IsHighQuality &&
            !confirmedListingIds.Contains((WorldName, x.ListingId)) &&
            GetPurchaseCost(x) <= SpendableGil() &&
            config.Fees.NetProceeds(template.TargetSalePrice, x.Quantity) >=
                GetPurchaseCost(x) * (1m + RequiredPurchaseRoi(template) / 100m) &&
            (decimal)config.Fees.NetProceeds(template.TargetSalePrice, x.Quantity) - GetPurchaseCost(x) >=
                (decimal)config.ProcurementMinimumProfitPerUnit * x.Quantity)
            .Select(x => new ProcurementMarketListing(x.ItemId, x.ListingId, x.RetainerId,
                WorldName, 0, x.PricePerUnit, x.Quantity, x.IsHighQuality)).ToArray();
        var refreshed = planner.BuildPlan(new(
            [demand with { Listings = live }], [rule], SpendableGil(), 1,
            Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve),
            config.ProcurementMinimumRoiPercent, config.ProcurementMinimumProfitPerUnit,
            HomeWorld: homeWorld, OwnedRetainerIds: retainerListings.OwnedRetainerIds,
            OwnedStock: CollectOwnedStock(), HighQualityOnly: config.BuyHighQualityOnly,
            ResaleListings: homePrices.GetValueOrDefault(template.ItemId) ?? [],
            Portfolio: config.PortfolioGates, PortfolioCapacitySlots: PortfolioCapacitySlots(),
            MarketTaxPercent: config.Fees.MarketTaxPercent,
            Economics: config.EconomicPolicy));
        if (refreshed.Orders.FirstOrDefault() is { } next)
        {
            currentOrder = next with
            {
                MaximumAcceptableUnitPrice = Math.Min(maximumPrice, next.MaximumAcceptableUnitPrice),
                TargetSalePrice = Math.Min(template.TargetSalePrice, next.TargetSalePrice),
                RequiredRoiPercent = Math.Max(template.RequiredRoiPercent, next.RequiredRoiPercent),
            };
            return true;
        }

        finishedBargainSweeps.Add((WorldName, template.ItemId, template.IsHighQuality));
        var units = CollectOwnedStock().Where(x => x.ItemId == template.ItemId &&
            x.IsHighQuality == template.IsHighQuality).Sum(x => (long)x.Quantity);
        log.Add(AutomationLogLevel.Information,
            $"BARGAIN SWEEP COMPLETE for {template.ItemName} on {WorldName}: no further live listing at or below " +
            $"{maximumPrice:N0} gil meets the current profit, budget, demand and bag-space checks. " +
            $"Holding {units:N0} resale units; {SpendableGil():N0} spendable gil remains. " +
            string.Join("; ", refreshed.DecisionLog.Where(x => !x.Selected).Select(x => x.Reason).Distinct()));
        AdvanceOrder();
        return false;
    }
}
