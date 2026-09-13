using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Services;
using Xunit;
using Route = SmartUndercutBot.Core.Tests.Automation.ProcurementControllerTests.Route;

namespace SmartUndercutBot.Core.Tests.Automation;

public sealed class ShoppingCadenceTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 12)]
    [InlineData(24, 60)]
    public void SevenMillionAndOpenShelvesStartShoppingEvenWithDeepBagStock(int free, int bagStacks)
    {
        using var run = StockedRoute();
        run.Repricing.LastKnownFreeSaleSlots = free;
        run.Repricing.ListedStock = [new(1, true, (uint)((60 - free) * 99), 60 - free)];
        run.Game.Inventory = bagStacks * 99;
        Assert.False(run.Controller.HoldingForResaleStock);
        Assert.False(run.Controller.HoldingForSaleSlots);
        Assert.True(run.Controller.PurchaseCapacity > 0);
        Assert.Null(run.Controller.ShoppingWaitReason);
        run.Tick();
        Assert.Equal(1, run.Game.Scans);
        Assert.True(run.Controller.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullShelvesAndBackupStockStillDepartWhenTheSlowerHuntIsDue(bool live)
    {
        using var run = StockedRoute();
        run.Config.Current.LiveWorldStockHuntEnabled = live;
        var due = run.Controller.Status.NextAutomaticScan;
        Assert.True(run.Controller.HoldingForResaleStock);
        run.Tick(3599);
        Assert.Empty(run.Game.Commands);
        Assert.Equal(0, run.Game.Scans);
        Assert.Equal(due, run.Controller.Status.NextAutomaticScan);
        run.Tick(1);
        for (var i = 0; i < 10 && run.Game.Commands.Count == 0; i++) run.Tick(2);
        Assert.True(run.Controller.IsActive);
        Assert.Equal(live ? 0 : 1, run.Game.Scans);
        Assert.NotEmpty(run.Game.Commands);
    }

    [Fact]
    public void RoutineCollectionsAndResumingAutomationDoNotRestartOrBypassTheStockedTimer()
    {
        using var run = StockedRoute();
        var due = run.Controller.Status.NextAutomaticScan;
        run.Tick(1800);
        run.Game.Gil += 500_000;
        run.Game.Inventory += 99;
        run.Controller.ResumeAutomatic();
        run.Tick();
        Assert.Equal(due, run.Controller.Status.NextAutomaticScan);
        Assert.Equal(0, run.Game.Scans);
        run.Tick(1800);
        Assert.Equal(1, run.Game.Scans);
    }

    [Fact]
    public void AnOpenSlotLiftsTheStockedCooldownImmediately()
    {
        using var run = StockedRoute();
        run.Tick(300);
        Assert.Equal(0, run.Game.Scans);
        run.Repricing.LastKnownFreeSaleSlots = 1;
        run.Tick();
        Assert.Equal(1, run.Game.Scans);
    }

    [Fact]
    public void MissingPreferredReplacementsLiftTheCooldownDespiteCheapFullShelves()
    {
        using var run = StockedRoute();
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        run.Game.Inventory = 11 * 99;
        Assert.Equal(1, run.Controller.PreferredRestockSlots);
        Assert.False(run.Controller.HoldingForResaleStock);
        run.Tick();
        Assert.Equal(1, run.Game.Scans);
    }

    [Fact]
    public void StockedHuntCanBuyADifferentPreferredDealAndReturnToUndercutting()
    {
        using var run = StockedRoute();
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 2, ItemName = "Potion", PreferredStock = true, RequireHighQuality = true,
            AllowHighQuality = true, HuntOnTour = true, TargetStackSize = 99, BagReserveQuantity = 0,
        });
        run.Repricing.ListedStock = [new(2, true, 60 * 99, 60)];
        run.Game.Inventory = 0;
        run.Game.OtherBagItems.Add(new(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
            0, 2, "Potion", 12 * 99, true, 999));
        run.Game.DemandMarkets = new uint[] { 1, 2 }.Select(item => new ProcurementMarketItem(item,
            item == 1 ? "Popcorn" : "Potion", [], Enumerable.Range(1, 3).Select(i =>
                new ProcurementSale(2000, 700, true, DateTimeOffset.UtcNow.AddDays(-i))).ToArray(),
            HqSalesPerDay: 700m)).ToArray();
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 100, 100, 2000, 99, true, 0)],
            "Cactuar" when item == 1 => [new(0, item, 200, 200, 1000, 99, true, 4950)],
            _ => [],
        };
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Tick(3600);
        for (var i = 0; i < 1000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(1u, Assert.Single(run.Game.Bought).Item);
        Assert.Equal("Siren", run.Game.World);
        Assert.True(run.Repricing.IsActive);
        Assert.True(run.Controller.HoldingForResaleStock);
        var scans = run.Game.Scans;
        // Simulate a completed retainer pass; its gil collection must not send
        // the character straight back out after a long stocked trip.
        run.Repricing.IsActive = false;
        run.Game.Gil += 200_000;
        run.Tick(3599);
        Assert.Equal(scans, run.Game.Scans);
        run.Tick(1);
        Assert.Equal(scans + 1, run.Game.Scans);
    }

    [Fact]
    public void FullOrdinaryBufferCanBeScoutedWithoutAuthorizingMoreOrdinaryStock()
    {
        using var run = StockedRoute(priority: false);
        run.Config.Current.ProcurementRules[0].PreferredStock = false;
        Assert.Equal(0, run.Controller.PurchaseCapacity);
        run.Tick(3600); run.Tick();
        Assert.Equal(2, run.Game.Scans); // Buying scope plus home resale data.
        Assert.Empty(run.Controller.Plan.Orders);
        Assert.Empty(run.Game.Commands);
        Assert.Equal(0, run.Game.Purchases);
        run.Tick(3599);
        Assert.Equal(2, run.Game.Scans);
        run.Tick(1); run.Tick();
        Assert.Equal(4, run.Game.Scans);
    }

    [Theory]
    [InlineData("gil")]
    [InlineData("bags")]
    [InlineData("retainers")]
    [InlineData("busy")]
    public void DueHuntsStillRequireResourcesAndACompletedRetainerPass(string blocked)
    {
        using var run = StockedRoute();
        if (blocked == "gil") run.Game.Gil = 250_000;
        if (blocked == "bags") run.Game.FreeInventorySlots = (uint)run.Config.Current.ProcurementInventoryReserve;
        if (blocked == "retainers") run.Repricing.LastKnownFreeSaleSlots = null;
        if (blocked == "busy") run.Controller.IsStartBlocked = () => true;
        run.Tick(7200);
        Assert.Equal(0, run.Game.Scans);
        Assert.Empty(run.Game.Commands);
    }

    [Fact]
    public void AStockedScoutDoesNotReturnImmediatelyAfterValuingItsFullBufferAtHome()
    {
        using var run = StockedRoute();
        run.Config.Current.ProcurementRules[0].PreferredStock = false;
        run.Config.Current.ProcurementBufferValueTarget = 1_000_000;
        run.Game.AutomaticWorldArrival = true;
        Assert.Equal(0, run.Controller.PurchaseCapacity);
        run.Tick(3600);
        for (var i = 0; i < 1000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.True(run.Controller.ResaleBagValue > run.Config.Current.ProcurementBufferValueTarget);
        Assert.Equal(8, run.Game.Searches.Where(x => x.World != "Siren").Select(x => x.World).Distinct().Count());
        Assert.Equal(0, run.Game.Purchases);
        Assert.Equal("Siren", run.Game.World);
    }

    private static Route StockedRoute(bool priority = true)
    {
        var run = new Route(priority);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 10;
        run.Config.Current.ShoppingTripMinimumGil = 1_000_000;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Config.Current.ProcurementRules[0].MaximumSaleSlots = 20;
        run.Config.Current.ProcurementRules[0].BagReserveQuantity = 0;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(1, true, 60 * 99, 60)];
        run.Game.Inventory = 12 * 99;
        run.Game.Gil = 7_000_000;
        return run;
    }
}
