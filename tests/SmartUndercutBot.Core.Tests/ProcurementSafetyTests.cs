using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class ProcurementSafetyTests
{
    [Theory]
    [InlineData(1050u, 20, 100u)]
    [InlineData(101u, 5.5, 1u)]
    [InlineData(1000u, 0, 500u)]
    public void ResaleFloorPreservesMarginAfterSellerTax(uint cost, double roi, uint profit)
    {
        var price = ProcurementPriceSafety.MinimumResalePrice(cost, (decimal)roi, profit);
        var conservativeNet = decimal.Floor(price * 0.95m);
        Assert.True(conservativeNet >= cost * (1m + (decimal)roi / 100m));
        Assert.True(conservativeNet - cost >= profit);
    }

    [Fact]
    public void NormalQualityPurchaseCannotUseHighQualitySales()
    {
        var market = new ProcurementMarketItem(1, "Item", [new(1, 1, 1, "Siren", 1, 1000, 99, false)],
            [new(100, 20, false, DateTimeOffset.UtcNow), new(10000, 99, true, DateTimeOffset.UtcNow),
             new(10000, 99, true, DateTimeOffset.UtcNow)]);
        var rule = new ProcurementRule { ItemId = 1, AllowHighQuality = true };
        Assert.Empty(new ProcurementPlannerService().BuildPlan(new([market], [rule], 500_000, 5, 5, 20, 100)).Orders);
    }

    [Fact]
    public void HighQualityVolumeCannotQualifySlowNormalQualityStock()
    {
        var market = new ProcurementMarketItem(1, "Item", [new(1, 1, 1, "Siren", 1, 1, 1, false)],
            [new(10000, 1, false, DateTimeOffset.UtcNow), new(10000, 99, true, DateTimeOffset.UtcNow)]);
        var rule = new ProcurementRule { ItemId = 1, AllowHighQuality = true };
        Assert.Empty(new ProcurementPlannerService().BuildPlan(new([market], [rule], 500_000, 5, 5, 20, 100)).Orders);
    }

    [Fact]
    public void LiveQualityPricesRemainSeparate()
    {
        var market = new ProcurementMarketItem(1, "Item",
            [new(1, 1, 1, "Siren", 1, 100, 99, false), new(1, 2, 2, "Siren", 1, 2000, 99, true),
             new(1, 3, 3, "Cactuar", 2, 1000, 99, true)], []);
        var rule = new ProcurementRule { ItemId = 1, AllowHighQuality = true };
        var order = Assert.Single(new ProcurementPlannerService().BuildLiveMarketPlan(new(
            [market], [rule], "Siren", new HashSet<ulong>(), 500_000, 5, 5, 20, 100)).Orders);
        Assert.True(order.IsHighQuality);
        Assert.Equal(2000u, order.TargetSalePrice);
    }

    [Fact]
    public void PendingPurchasesReserveCapacityAndCannotBeReplacedByBagQueue()
    {
        var ledger = new ProcurementLedger();
        ledger.RecordPurchase(new(1, "Item", 1, 1, "Siren", 1, 100, 100, true, 200, 150, 1000, 2), 99);
        Assert.Equal(2, ledger.PendingSaleSlots);
        ledger.QueueExistingStock(1, "Item", 999, 300, 99, true, 100);
        var entry = Assert.Single(ledger.Snapshot());
        Assert.False(entry.IsBagStock);
        Assert.Equal(100u, entry.PendingQuantity);
        ledger.MarkListed(1, true, 99);
        Assert.Equal(1, ledger.PendingSaleSlots);
        ledger.MarkListed(1, true, uint.MaxValue);
        Assert.Equal(0, ledger.PendingSaleSlots);
    }
}
