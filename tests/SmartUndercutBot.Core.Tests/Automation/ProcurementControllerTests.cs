using System.Numerics;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests.Automation;

public sealed class ProcurementControllerTests
{
    [Theory]
    [InlineData(2000u, 1)]
    [InlineData(900u, 0)]
    [InlineData(0u, 0)]
    public void LongCircuitsRefreshHomePricesBeforeBuyingAndRejectCollapsedOrMissingMarkets(uint refreshedPrice, int expectedBuys)
    {
        using var run = new Route(priority: true);
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Config.Current.PriorityWorldsPerTrip = 31;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        var aged = false;
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" when aged && refreshedPrice == 0 => [],
            "Siren" => [new(0, item, 11, 21, aged ? refreshedPrice : 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20, 1000, 99, true, 4950)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 1500 && run.Controller.IsActive; i++)
        {
            if (!aged && run.Controller.RecentPrices.Any(x => x.World == "Cactuar"))
            {
                aged = true;
                run.Tick(1860); // old home quote is now unusable; finish the scout circuit
            }
            else run.Tick(2);
        }
        Assert.True(aged);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(31, run.Game.Searches.Where(x => x.World != "Siren").Select(x => x.World).Distinct().Count());
        Assert.Equal(2, run.Game.Searches.Count(x => x.World == "Siren"));
        Assert.Equal(expectedBuys, run.Game.Purchases);
        Assert.Equal("Siren", run.Game.World);
        Assert.Contains(run.Log.Messages, x => x.Contains("Refreshing 1 home resale price"));
    }

    [Fact]
    public void FailedHomeRefreshCannotBuyAgainstTheOldAnchor()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        var aged = false;
        run.Controller.RunNow();
        for (var i = 0; i < 800 && run.Controller.IsActive; i++)
        {
            if (!aged && run.Controller.RecentPrices.Any(x => x.World == "Cactuar"))
            {
                aged = true;
                run.Tick(1860);
            }
            else
            {
                if (aged && run.Game.World == "Siren") run.Game.ListingsReady = false;
                run.Tick(2);
            }
        }
        Assert.True(aged);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(0, run.Game.Purchases);
        Assert.Contains(run.Log.Messages, x => x.Contains("SKIPPED Popcorn on Siren"));
    }

    [Fact]
    public void FourMillionAndFullMateriaShelvesAutomaticallyShopForMissingPreferredStock()
    {
        using var run = new Route(priority: true);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 10;
        run.Config.Current.ShoppingTripMinimumGil = 1_000_000;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 1200, 60)];
        run.Game.Gil = 4_000_000;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.DemandMarkets = [new(1, "Popcorn", [],
            Enumerable.Range(1, 3).Select(i => new ProcurementSale(15_000, 700, true,
                DateTimeOffset.UtcNow.AddDays(-i))).ToArray(), HqSalesPerDay: 300m)];
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 15_000, 99, true, 0)],
            // About 13% net ROI; a 20% check or the old 799k budget rejects it.
            "Cactuar" => [new(0, item, 10, 20, 12_000, 99, true, 59_400)],
            _ => [],
        };
        Assert.Equal(3_995_000u, run.Controller.ShoppingBudget);
        Assert.Equal(12, run.Controller.PreferredRestockSlots);
        Assert.False(run.Controller.HoldingForSaleSlots);
        Assert.Null(run.Controller.ShoppingWaitReason);
        run.Tick();
        for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(1_247_400u, run.Controller.Status.GilSpent);
        Assert.Equal(2_752_600u, run.Game.Gil);
        Assert.Equal("Siren", run.Game.World);
        // The newly bought fast mover must also be sellable below the old 20% floor.
        Assert.InRange(run.Config.Current.PerItemRules[1].AcquisitionFloor, 14_000u, 14_999u);
    }

    [Fact]
    public void DeepPreferredBufferStopsAdaptiveTripsEvenBeforeDemandIsLoaded()
    {
        using var run = new Route(priority: true);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 10;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        run.Game.Inventory = 12 * 99;
        run.Game.Gil = 4_000_000;
        Assert.Equal(0, run.Controller.PreferredRestockSlots);
        Assert.True(run.Controller.HoldingForSaleSlots);
        run.Tick(601);
        Assert.Equal(0, run.Game.Scans);
        Assert.Empty(run.Game.Commands);
    }

    [Fact]
    public void CheapTradingBufferCannotConsumeThePreferredReplacementAllowance()
    {
        using var run = new Route();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Config.Current.ProcurementRules.Add(new() { ItemId = 2, ItemName = "Materia", TargetStackSize = 20,
            MaximumSaleSlots = 20, BagReserveQuantity = 0 });
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(2, false, 1200, 60)];
        run.Game.OtherBagItems.Add(new(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
            1, 2, "Materia", 240, false, 999));
        Assert.Equal(12, run.Controller.ResaleBagSlots);
        Assert.Equal(12, run.Controller.PurchaseCapacity);
        run.Game.FreeInventorySlots = (uint)run.Config.Current.ProcurementInventoryReserve;
        Assert.Equal(0, run.Controller.PurchaseCapacity);
        Assert.Contains("bag space", run.Controller.ShoppingWaitReason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AdaptiveTripsRespectFixedBufferModeAndCompleteRetainerChecks(bool continuous, bool allRetainers)
    {
        using var run = new Route();
        run.Config.Current.ContinueShoppingWhenStocked = continuous;
        run.Config.Current.ProcessAllRetainers = allRetainers;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 10;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        Assert.Equal(0, run.Controller.PreferredRestockSlots);
        Assert.True(run.Controller.HoldingForSaleSlots);
    }

    [Fact]
    public void SaleOnlyBacklogCannotBlockComfortableStockShoppingOnFullRetainers()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        run.Config.Current.ProcurementRules.Add(new() { ItemId = 5000, ItemName = "Ether", LiquidateOnly = true,
            ListFromBags = true, TargetStackSize = 5, BagReserveQuantity = 0 });
        // 4,660 units across five actual slots, previously displayed as 932 stacks.
        for (ushort slot = 0; slot < 5; slot++)
            run.Game.OtherBagItems.Add(new(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
                slot, 5000, "Ether", slot < 4 ? 999u : 664u, false, 999));
        run.Ledger.QueueExistingStock(5000, "Ether", 4660, 1000, 5, false, 0, maximumListingSlots: 2);
        Assert.Equal(5, run.Controller.MarketableBagSlots);
        Assert.Equal(0, run.Controller.ResaleBagSlots);
        Assert.Equal(12, run.Controller.ComfortableStockTarget);
        Assert.Equal(12, run.Controller.PurchaseCapacity);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Tick();
        for (var i = 0; i < 200 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Contains("/li Cactuar", run.Game.Commands);
        Assert.Equal("Siren", run.Game.World);
        Assert.DoesNotContain(5000u, run.Game.ScannedItemIds);
    }

    [Fact]
    public void PackedBagStackIsDisplayedOnceAndTradingLotsUseTheSaleQuantity()
    {
        using var run = new Route();
        run.Config.Current.ProcurementRules[0].ItemName = "Caramel Popcorn";
        run.Game.Inventory = 397; // 100 personal + three sale stacks of 99, in one physical slot.
        Assert.Equal(1, run.Controller.MarketableBagSlots);
        Assert.Equal(3, run.Controller.ResaleBagSlots);
    }

    [Fact]
    public void FullRetainersSearchOnScheduleEvenWhenComfortableStockIsReady()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        // Buffer stacks are capped per item by that item's allowed sale slots, so
        // give this one enough room for the twelve stacks the target expects.
        run.Config.Current.ProcurementRules[0].MaximumSaleSlots = 20;
        run.Game.Inventory = 12 * 99;
        Assert.Equal(0, run.Controller.PurchaseCapacity);
        run.Tick(); run.Tick();
        Assert.Equal(2, run.Game.Scans);
        Assert.Equal(0, run.Game.Purchases);
        for (var i = 0; i < 50; i++) run.Tick();
        Assert.Equal(2, run.Game.Scans);
        run.Tick(601); run.Tick();
        Assert.Equal(4, run.Game.Scans);
        Assert.Equal(0, run.Game.Purchases);
        Assert.Contains("comfortable trading stock", run.Controller.Status.Detail);
    }

    [Fact]
    public void RestockingFollowsDemandCoverageRatherThanTheFixedSlotCap()
    {
        // 990 units listed against roughly 1,414 units a day is about 0.7 days of
        // cover, far short of the target. The old rule stopped at three spare
        // stacks because the item was already on eight sale slots; the demand-based
        // rule keeps buying, because the market plainly absorbs it.
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(1, true, 990, 10), new(999, false, 50, 50)];
        run.Game.Gil = 10_000_000;
        run.Game.WeeklySalesQuantity = 9_900;
        run.Game.ExtraBuyListings = 10;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Tick();
        for (var i = 0; i < 300 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.True(run.Game.Purchases > 3,
            $"a market this liquid should restock past the old three-stack clamp, bought {run.Game.Purchases}");
        Assert.Equal(run.Game.Purchases * 99, run.Game.Inventory);
        // The rule's own eight-slot limit is untouched; it is a floor now, and the
        // demand-derived cap is what actually governs.
        Assert.Equal(8, run.Config.Current.ProcurementRules[0].MaximumSaleSlots);
        Assert.True(ProcurementPlannerService.EffectiveMaximumSlots(
            run.Config.Current.EconomicPolicy,
            run.Config.Current.ProcurementRules[0],
            new(1, "Popcorn", 0, 0, "Cactuar", 0, 1_000, 99, true, 1_999, 1_999, 20_000, 1,
                SalesPerDay: 9_900m / 7m, Tier: PortfolioTier.Secondary)) > 8);
    }

    [Fact]
    public void RestockingStopsOnceDemandCoverageIsSatisfied()
    {
        // Same market, but the shelves are already full: 6,000 units against about
        // 1,414 a day is over four days of cover, past the target and its overshoot
        // allowance. Nothing more is bought however good the listing looks.
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(1, true, 6_000, 10)];
        run.Game.Gil = 10_000_000;
        run.Game.WeeklySalesQuantity = 9_900;
        run.Game.ExtraBuyListings = 10;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Tick();
        for (var i = 0; i < 300 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(0, run.Game.Purchases);
        Assert.Empty(run.Controller.Plan.Orders);
    }

    [Fact]
    public void ZeroGilWaitsWithoutTripsAndWakesAsSoonAsIncomeArrives()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Game.Gil = 0;
        for (var i = 0; i < 72; i++) run.Tick(3600);
        Assert.Equal(0, run.Game.Scans);
        Assert.Empty(run.Game.Commands);
        Assert.Contains("income", run.Controller.ShoppingWaitReason);
        run.Game.Gil = 500_000;
        run.Tick();
        Assert.Equal(ProcurementState.ScanningUniversalis, run.Controller.State);
    }

    [Fact]
    public void AFewFreeSlotsAreNotWorthATripAndUndercuttingContinuesUntilABatchOpens()
    {
        using var run = new Route(priority: true);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 10;
        run.Game.Gil = 10_000_000;
        run.Repricing.LastKnownFreeSaleSlots = 3;

        for (var i = 0; i < 24; i++) run.Tick(3600);
        Assert.Empty(run.Game.Commands);
        Assert.Contains("free sale slots", run.Controller.ShoppingWaitReason);
        Assert.True(run.Controller.HoldingForSaleSlots);

        // Undercutting frees a worthwhile batch, and only then does a trip depart.
        run.Repricing.LastKnownFreeSaleSlots = 12;
        Assert.False(run.Controller.HoldingForSaleSlots);
        Assert.Null(run.Controller.ShoppingWaitReason);
    }

    [Fact]
    public void PocketChangeIsNotWorthATripEvenWithPlentyOfEmptySlots()
    {
        using var run = new Route(priority: true);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ShoppingTripMinimumGil = 1_000_000;
        run.Repricing.LastKnownFreeSaleSlots = 40;
        run.Game.Gil = 250_000;

        for (var i = 0; i < 24; i++) run.Tick(3600);
        Assert.Empty(run.Game.Commands);
        Assert.True(run.Controller.HoldingForGil);
        Assert.Contains("gil", run.Controller.ShoppingWaitReason);

        run.Game.Gil = 4_000_000;
        Assert.False(run.Controller.HoldingForGil);
        Assert.Null(run.Controller.ShoppingWaitReason);
    }

    [Fact]
    public void BothHoldsCanBeTurnedOffAndRestoreTheOlderShopAnyVacancyBehaviour()
    {
        using var run = new Route(priority: true);
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 0;
        run.Config.Current.ShoppingTripMinimumGil = 0;
        run.Repricing.LastKnownFreeSaleSlots = 1;
        run.Game.Gil = 20_000;

        Assert.False(run.Controller.HoldingForSaleSlots);
        Assert.False(run.Controller.HoldingForGil);
    }

    [Fact]
    public void NewIncomeRetriesAnUnaffordablePlanBeforeTheNormalInterval()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Game.Gil = 6_000;
        run.Tick(); run.Tick();
        Assert.Equal(ProcurementState.PlanReady, run.Controller.State);
        Assert.Empty(run.Controller.Plan.Orders);
        var scans = run.Game.Scans;
        run.Tick(1);
        Assert.Equal(scans, run.Game.Scans);
        run.Game.Gil = 500_000;
        run.Tick(1);
        Assert.Equal(scans + 2, run.Game.Scans);
    }

    [Fact]
    public void ExistingBagBufferStillCountsAfterAnEmptyLedgerOrReload()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ProcurementBagBufferStacks = 2;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Game.Inventory = 198;
        Assert.Empty(run.Ledger.Snapshot());
        Assert.Equal(2, run.Controller.ResaleBagSlots);
        run.Tick(601);
        Assert.Equal(0, run.Game.Scans);
        Assert.Equal(0, run.Controller.PurchaseCapacity);
    }

    [Fact]
    public void BoughtInventoryAndItsLedgerEntryCountOnce()
    {
        using var run = new Route();
        run.Game.Inventory = 99;
        run.Ledger.RecordPurchase(new(1, "Popcorn", 1, 1, "Siren", 1, 1_000, 99, true, 2_000, 1_500, 100, 1), 99);
        Assert.Equal(1, run.Controller.ResaleBagSlots);
        Assert.Equal(4, run.Controller.PurchaseCapacity);
    }

    [Fact]
    public void PersonalConsumableReserveDoesNotFillTheResaleBuffer()
    {
        using var run = new Route();
        run.Config.Current.ProcurementRules[0].ItemName = "Caramel Popcorn";
        run.Game.Inventory = 100;
        Assert.Equal(0, run.Controller.ResaleBagSlots);
    }

    [Fact]
    public void SaturatedBufferBudgetIncludesTheCostOfStockAlreadyBought()
    {
        using var run = new Route();
        run.Repricing.LastKnownFreeSaleSlots = 0;
        Assert.Equal(199_000u, run.Controller.ShoppingBudget);
        run.Game.Gil -= 103_950;
        run.Game.Inventory = 99;
        run.Config.Current.PerItemRules[1] = new() { CostBasis = 1_050 };
        Assert.Equal(95_050u, run.Controller.ShoppingBudget);
        // A later trip or cleared in-memory ledger cannot reset the allowance.
        run.Controller.ResumeAutomatic();
        Assert.Equal(95_050u, run.Controller.ShoppingBudget);
        run.Repricing.LastKnownFreeSaleSlots = 5;
        Assert.Equal(891_050u, run.Controller.ShoppingBudget);
    }

    [Theory]
    [InlineData("Bismarck")]
    [InlineData("Ravana")]
    [InlineData("Sephirot")]
    [InlineData("Sophia")]
    [InlineData("Zurvan")]
    public void OceanicDealsCannotEnterAnAutomaticRoute(string world)
    {
        using var run = new Route();
        run.Game.BuyingWorld = world;
        run.Controller.RunNow();
        run.Tick();
        Assert.Empty(run.Controller.Plan.Orders);
        Assert.Empty(run.Game.Commands);
    }

    [Fact]
    public void RuleChangedToSellOnlyDuringTravelCannotBePurchased()
    {
        using var run = new Route();
        run.ReachListings();
        run.Config.Current.ProcurementRules[0].LiquidateOnly = true;
        run.Tick();
        Assert.Equal(0, run.Game.Purchases);
    }

    [Fact]
    public void EmptyHomeScanExplainsWhyShoppingReturnedWithoutBuying()
    {
        using var run = new Route();
        run.Game.AutomaticWorldArrival = true;
        run.Game.MissingHomeListings = true;
        run.Controller.RunLiveStockHuntNow();
        for (var tick = 0; tick < 2_000 && run.Controller.IsActive; tick++)
            run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(0, run.Game.Purchases);
        Assert.Contains("No purchases: no usable resale prices", run.Controller.Status.Detail);
        Assert.Contains("Siren", run.Controller.Status.Detail);
    }
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public void LiveAllWorldTourWithoutDemandDoesNotBuy(bool cheapOversizedHomeStack, int expectedPurchases)
    {
        using var run = new Route();
        run.Game.AutomaticWorldArrival = true;
        run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.CheapOversizedHomeStack = cheapOversizedHomeStack;
        run.Controller.RunLiveStockHuntNow();
        for (var tick = 0; tick < 2_000 && run.Controller.IsActive; tick++)
            run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(expectedPurchases, run.Game.Purchases);
        Assert.DoesNotContain("/li Ravana", run.Game.Commands);
        Assert.Contains("/li Seraph", run.Game.Commands);
        Assert.Equal("Siren", run.Game.World);
        Assert.Equal(1, run.Repricing.Starts);
    }

    [Fact]
    public void TimedOutLiveWorldAbortsOldTravelBeforeSchedulingNextWorld()
    {
        using var run = new Route();
        run.Controller.RunLiveStockHuntNow();
        run.Tick(2);
        run.Game.IsBusy = true;
        run.Tick(601);
        Assert.Equal(1, run.Game.Aborts);
        Assert.Equal(ProcurementState.WaitingBeforeWorldTravel, run.Controller.State);
        Assert.Single(run.Game.Commands);
        run.Tick(2);
        Assert.Equal(2, run.Game.Commands.Count);
    }
    [Fact]
    public void RouteTravelsPurchasesAndReturnsToRetainerPass()
    {
        using var run = new Route();
        run.ReachPurchase();
        Assert.Equal(1, run.Game.Purchases);
        run.Game.Inventory = 99;
        run.Tick();
        Assert.Equal(103_950u, run.Controller.Status.GilSpent);
        Assert.Equal(99u, Assert.Single(run.Ledger.Snapshot()).PendingQuantity);
        Assert.Equal(1_050u, run.Config.Current.PerItemRules[1].CostBasis);
        run.Tick(2);
        Assert.Equal(ProcurementState.WaitingBeforeHomeTravel, run.Controller.State);
        run.Tick(2);
        Assert.Equal("/li Siren", run.Game.Commands.Last());
        run.Game.World = "Siren";
        run.Tick();
        run.Tick(9);
        run.Tick();
        run.Tick(13);
        run.Tick(); // Close the market board opened by /li mb before using the bell.
        run.Tick(2);
        run.Tick();
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(1, run.Repricing.Starts);
    }

    [Fact]
    public void StopAbortsTravelAndDoesNotAutomaticallyRestart()
    {
        using var run = new Route();
        run.ReachPurchase();
        run.Config.Current.AutomaticProcurementEnabled = true;
        run.Controller.Halt();
        run.Game.BellOpen = true;
        run.Tick(40_000);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
        Assert.Equal(1, run.Game.Aborts);
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void RepeatedRunCommandCannotResetAnActivePurchase()
    {
        using var run = new Route();
        run.ReachPurchase();
        run.Controller.RunNow();
        Assert.Equal(ProcurementState.WaitingForPurchase, run.Controller.State);
        run.Tick(31);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
        Assert.Contains("OUTCOME UNKNOWN", run.Controller.Status.Detail);
        Assert.Equal(1, run.Game.Purchases);
    }

    [Theory]
    [InlineData("disarmed")]
    [InlineData("world")]
    [InlineData("inventory")]
    [InlineData("board")]
    public void RechecksGuardsImmediatelyBeforeBuying(string changed)
    {
        using var run = new Route();
        run.ReachListings();
        switch (changed)
        {
            case "disarmed": run.Config.Current.AllowAutomaticPurchases = false; break;
            case "world": run.Game.World = "Siren"; break;
            case "inventory": run.Game.FreeInventorySlots = 10; break;
            case "board": run.Game.BoardOpen = false; break;
        }
        run.Tick();
        // Abandoning the buy is not a reason to abandon the character on a foreign
        // world: the route gives up on shopping and heads home to the bell instead.
        Assert.Equal(0, run.Game.Purchases);
        Assert.Contains(run.Controller.State, new[]
        {
            ProcurementState.WaitingBeforeHomeTravel, ProcurementState.WaitingAfterHomeArrival,
        });
        Assert.True(run.Controller.IsActive);
    }

    [Fact]
    public void FailedInteractionTimesOutEvenWhenObjectIsInRange()
    {
        using var run = new Route();
        run.Game.OpenBoardOnLocalTravel = false;
        run.Game.InteractionSucceeds = false;
        run.ReachApproach();
        run.Tick();
        run.Tick(61);
        Assert.Equal(ProcurementState.WaitingBeforeHomeTravel, run.Controller.State);
        Assert.Equal(0, run.Game.Purchases);
    }

    [Fact]
    public void AlreadyOpenBoardIsUsedWithoutAnotherInteraction()
    {
        using var run = new Route();
        run.Game.InteractionSucceeds = false;
        run.ReachApproach();
        run.Game.BoardOpen = true;
        run.Tick(); run.Tick(); run.Tick(4); run.Tick();
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void ArrivalCannotWaitForeverForLifestream()
    {
        using var run = new Route();
        run.Begin();
        run.Game.World = "Cactuar";
        run.Tick();
        run.Game.IsBusy = true;
        run.Tick(70);
        Assert.Equal(ProcurementState.WaitingBeforeHomeTravel, run.Controller.State);
        Assert.Equal(1, run.Game.Aborts);
    }

    [Fact]
    public void CrossDataCenterLogoutWaitsForArrival()
    {
        using var run = new Route();
        run.Begin();
        run.Game.IsLoaded = false;
        run.Game.IsBusy = true;
        run.Tick(200);
        Assert.Equal(ProcurementState.WaitingForWorld, run.Controller.State);
        run.Game.IsLoaded = true;
        run.Game.IsBusy = false;
        run.Game.World = "Cactuar";
        run.Tick();
        Assert.Equal(ProcurementState.WaitingAfterWorldArrival, run.Controller.State);
    }

    [Fact]
    public void BusyOtherControllerPreventsStartingRoute()
    {
        using var run = new Route();
        run.Controller.IsStartBlocked = () => true;
        run.Controller.RunNow();
        Assert.Equal(ProcurementState.Idle, run.Controller.State);
        Assert.Empty(run.Game.Commands);
    }

    [Fact]
    public void LiveTaxMustStillMeetProfitGuards()
    {
        using var run = new Route();
        run.ReachListings();
        run.Game.BuyerTax = 99_000;
        run.Tick();
        Assert.Equal(0, run.Game.Purchases);
        Assert.Empty(run.Ledger.Snapshot());
    }

    [Fact]
    public void TimedOutSearchRetriesAndCanStillPurchase()
    {
        using var run = new Route();
        run.ReachListings();
        run.Game.ListingsReady = false;
        run.Tick(31);
        Assert.Equal(ProcurementState.WaitingForListings, run.Controller.State);
        Assert.Contains("attempt 2/3", run.Controller.Status.Detail);
        Assert.Equal(0, run.Game.Purchases);
        run.Game.ListingsReady = true;
        run.Tick(3);
        Assert.Equal(ProcurementState.WaitingForPurchase, run.Controller.State);
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void ExhaustedSearchRetriesSkipWithoutSubmittingPurchase()
    {
        using var run = new Route();
        run.ReachListings();
        run.Game.ListingsReady = false;
        run.Tick(31);
        run.Tick(33);
        run.Tick(33);
        run.Tick(2);
        Assert.Equal(ProcurementState.WaitingBeforeHomeTravel, run.Controller.State);
        Assert.Equal(0, run.Game.Purchases);
    }

    [Fact]
    public void RecoverableStopDoesNotDisableUnattendedProcurementForever()
    {
        using var run = new Route();
        run.Repricing.LastKnownFreeSaleSlots = null;
        run.Controller.RunLiveStockHuntNow();
        Assert.Equal(ProcurementState.Halted, run.Controller.State);

        run.Tick(60);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);

        // Once the retry delay passes and the character is back at a bell, the
        // controller must be schedulable again instead of staying stopped.
        run.Repricing.LastKnownFreeSaleSlots = 5;
        run.Tick(600);
        Assert.Equal(ProcurementState.Idle, run.Controller.State);
    }

    [Fact]
    public void UserStopStaysStoppedAndIsNeverRetriedAutomatically()
    {
        using var run = new Route();
        run.Controller.Halt();
        run.Tick(100_000);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
    }

    [Fact]
    public void StoppedRouteDoesNotResumeAwayFromASummoningBell()
    {
        using var run = new Route();
        run.Repricing.LastKnownFreeSaleSlots = null;
        run.Controller.RunLiveStockHuntNow();
        run.Game.BellOpen = false;
        run.Tick(100_000);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
    }

    [Fact]
    public void PendingStockDoesNotMakeTheSchedulerRescanUniversalisContinuously()
    {
        using var run = new Route();
        run.Config.Current.AutomaticProcurementEnabled = true;
        run.Config.Current.LiveWorldStockHuntEnabled = false;
        run.Config.Current.ProcurementBagBufferStacks = 0;
        run.Repricing.LastKnownFreeSaleSlots = 5;
        // Five bought-but-unlisted stacks reserve every free sale slot.
        run.Ledger.RecordPurchase(new(1, "Popcorn", 1, 1, "Siren", 1, 1_000, 5, true, 2_000, 1_500, 100, 1), 1);
        Assert.Equal(5, run.Ledger.PendingSaleSlots);

        run.Tick();
        run.Tick();
        Assert.Equal(ProcurementState.Idle, run.Controller.State);
        Assert.Equal(0, run.Game.Scans);

        // The "capacity changed" trigger must not fire on its own reserved slots,
        // or the scheduler rescans as fast as Universalis answers.
        for (var tick = 0; tick < 50; tick++)
            run.Tick();
        Assert.Equal(0, run.Game.Scans);

        // Listing that stock frees the capacity again and earns a fresh scan.
        run.Ledger.MarkListed(1, true, 5);
        run.Tick();
        run.Tick();
        Assert.Equal(2, run.Game.Scans);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void AutomaticShoppingNeedsConfirmedEmptySlots(int? freeSlots)
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        // No completed pass, or a completed pass with no capacity and no bag buffer.
        run.Config.Current.ProcurementBagBufferStacks = 0;
        run.Repricing.LastKnownFreeSaleSlots = freeSlots;
        run.Tick(100_000);
        Assert.Equal(0, run.Game.Scans);
        Assert.Empty(run.Game.Commands);
    }

    [Fact]
    public void SoldSlotStartsShoppingBeforePeriodicScanIsDue()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ProcurementBagBufferStacks = 0;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Tick();
        Assert.Equal(0, run.Game.Scans);
        run.Repricing.LastKnownFreeSaleSlots = 1;
        run.Tick();
        Assert.Equal(ProcurementState.ScanningUniversalis, run.Controller.State);
        Assert.Equal(2, run.Game.Scans); // Buying scope plus home resale data.
    }

    [Fact]
    public void RetainerSafetyStopPreventsAutomaticShopping()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Repricing.Halt("Listing verification failed.");
        run.Tick(100_000);
        Assert.Equal(0, run.Game.Scans);
    }

    [Fact]
    public void CompletedRouteCanRestockAgainAfterItsPurchasesAreListedAndSell()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ProcurementBagBufferStacks = 0;
        run.Game.AutomaticWorldArrival = true;
        run.Game.AutomaticPurchaseConfirmation = true;
        run.Repricing.LastKnownFreeSaleSlots = 1;
        run.Tick();
        for (var tick = 0; tick < 200 && run.Controller.IsActive; tick++)
            run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(1, run.Repricing.Starts);

        run.Ledger.MarkListed(1, true, 99);
        // Listing moves the stack out of the bags and the sale clears the retainer,
        // so the item is no longer owned exposure and may be restocked.
        run.Game.Inventory = 0;
        run.Repricing.IsActive = false;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Tick(601);
        Assert.Equal(2, run.Game.Scans);
        run.Repricing.LastKnownFreeSaleSlots = 1;
        run.Tick();
        for (var tick = 0; tick < 200 && run.Controller.IsActive; tick++)
            run.Tick(2);
        Assert.Equal(2, run.Game.Purchases);
        Assert.Equal(2, run.Repricing.Starts);
        Assert.Equal("Siren", run.Game.World);
    }

    [Fact]
    public void FailedReturnTripRetriesHomeWithoutBuyingAgain()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.ReachPurchase();
        run.Game.Inventory = 99;
        for (var tick = 0; tick < 10 && run.Controller.State != ProcurementState.WaitingForHomeWorld; tick++)
            run.Tick(2);
        Assert.Equal(ProcurementState.WaitingForHomeWorld, run.Controller.State);
        run.Tick(601); // The first home transfer times out on the visited world.
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
        Assert.True(run.Controller.IsWaitingToReturnHome);
        Assert.False(run.Controller.RequiresManualRestart);
        run.Game.AutomaticWorldArrival = true;
        run.Tick(601);
        for (var tick = 0; tick < 100 && run.Controller.IsActive; tick++)
            run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal("Siren", run.Game.World);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(1, run.Repricing.Starts);
    }

    [Fact]
    public void UnifiedStartResumesStoppedControllersAndChecksRetainersFirst()
    {
        using var run = new Route();
        var bags = new BagListingController();
        var stock = new StockAutomationController(run.Config, run.Repricing, run.Controller, bags);
        run.Config.Current.ProcurementBudget = 123_000;
        run.Config.Current.BagListingReservePerItem = 250;
        stock.Stop();
        Assert.False(run.Config.Current.KeepsRetainersStocked);
        Assert.True(bags.IsSuspended);
        Assert.False(stock.NeedsAttention);
        Assert.True(stock.Start());
        Assert.True(run.Config.Current.KeepsRetainersStocked);
        Assert.False(run.Controller.RequiresManualRestart);
        Assert.False(run.Repricing.RequiresManualRestart);
        Assert.False(bags.IsSuspended);
        Assert.Equal(1, run.Repricing.Starts);
        run.Tick(601);
        Assert.Equal(0, run.Game.Scans); // Repricing still owns the UI.
        Assert.Equal(123_000u, run.Config.Current.ProcurementBudget);
        Assert.Equal(250, run.Config.Current.BagListingReservePerItem);
    }

    [Fact]
    public void UnifiedStopDisarmsRecurringWorkAndCannotRestartFromBellReopen()
    {
        using var run = new Route();
        var bags = new BagListingController();
        var stock = new StockAutomationController(run.Config, run.Repricing, run.Controller, bags);
        Assert.True(stock.Start());
        stock.Stop();
        run.Game.BellOpen = false;
        run.Tick();
        run.Game.BellOpen = true;
        run.Tick(100_000);
        Assert.False(run.Config.Current.AutomationEnabled);
        Assert.False(run.Config.Current.RepeatBellRuns);
        Assert.False(run.Config.Current.AllowAutomaticPurchases);
        Assert.False(run.Config.Current.AllowAutomaticListing);
        Assert.False(run.Config.Current.AllowAutomaticWrites);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
        Assert.True(bags.IsSuspended);
        Assert.Equal(0, run.Game.Scans);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnifiedStartDoesNotArmWhenBellIsClosedOrAnotherOperationIsBusy(bool bellOpen, bool busy)
    {
        using var run = new Route();
        var bags = new BagListingController { IsRetainerListOpen = bellOpen, IsBusy = busy };
        var stock = new StockAutomationController(run.Config, run.Repricing, run.Controller, bags);
        Assert.False(stock.Start());
        Assert.NotNull(stock.StartIssue);
        Assert.False(run.Config.Current.KeepsRetainersStocked);
        Assert.Equal(0, run.Repricing.Starts);
    }

    [Fact]
    public void MissingHomeResaleDataDoesNotStartTravel()
    {
        using var run = new Route();
        run.Game.MissingHomeListings = true;
        run.Controller.RunNow();
        run.Tick();
        Assert.Empty(run.Controller.Plan.Orders);
        Assert.Empty(run.Game.Commands);
    }

    [Fact]
    public void SubmissionExceptionNeverRetriesAnUncertainPurchase()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Game.ThrowAfterPurchaseSubmission = true;
        run.ReachPurchase();
        Assert.Equal(1, run.Game.Purchases);
        Assert.True(run.Controller.RequiresManualRestart);
        Assert.Contains("OUTCOME UNKNOWN", run.Controller.Status.Detail);
        run.Game.BellOpen = true;
        run.Tick(100_000);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(ProcurementState.Halted, run.Controller.State);
    }

    [Fact]
    public void RunningOutOfGilMidRouteReturnsHomeToCollectRetainerSales()
    {
        using var run = new Route();
        run.ReachListings();
        // Only the travel reserve is left, so no world on the route is affordable.
        run.Game.Gil = run.Config.Current.ProcurementTravelReserve;
        run.Tick();
        Assert.Equal(0, run.Game.Purchases);
        Assert.Contains(run.Log.Messages, x => x.Contains("Out of spendable gil"));
        Assert.Contains(run.Controller.State, new[]
        {
            ProcurementState.WaitingBeforeHomeTravel, ProcurementState.WaitingAfterHomeArrival,
        });
    }

    [Fact]
    public void SellOnlyStockIsNeverAskedAboutInTheDealScan()
    {
        using var run = new Route();
        // Seeding every dye and materia as sell-only stock is what turned this scan
        // into hundreds of item ids per scope, and Universalis answered 504.
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 5_000, ItemName = "Dalamud Red Dye", LiquidateOnly = true, ListFromBags = true,
        });
        run.Controller.ScanNow();
        run.Tick();
        Assert.NotEmpty(run.Game.ScannedItemIds);
        Assert.Contains(1u, run.Game.ScannedItemIds);
        Assert.DoesNotContain(5_000u, run.Game.ScannedItemIds);
    }

    [Fact]
    public void FullRetainersStillBuyABagBufferReadyForTheNextSale()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.LiveWorldStockHuntEnabled = false;
        run.Config.Current.ProcurementBagBufferStacks = 2;
        run.Game.AutomaticWorldArrival = true;
        run.Game.AutomaticPurchaseConfirmation = true;
        // Every retainer slot is taken, which used to stop shopping completely.
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Tick(601);
        for (var tick = 0; tick < 400 && run.Game.Purchases == 0; tick++)
            run.Tick(2);
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void TheBagBufferStopsOnceEnoughStockIsWaiting()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.LiveWorldStockHuntEnabled = false;
        run.Config.Current.ProcurementBagBufferStacks = 2;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        // Two stacks are already bought and waiting, which fills the buffer.
        run.Ledger.RecordPurchase(new(1, "Popcorn", 1, 1, "Siren", 1, 1_000, 2, true, 2_000, 1_500, 100, 1), 1);
        Assert.Equal(2, run.Ledger.PendingSaleSlots);
        run.Tick(601);
        for (var tick = 0; tick < 200 && run.Controller.IsActive; tick++)
            run.Tick(2);
        Assert.Equal(0, run.Game.Purchases);
    }

    [Fact]
    public void ADeepStackIsNotMistakenForAWholeTradingBuffer()
    {
        using var run = new Route();
        run.Config.Current.EnableStockAutomation();
        run.Repricing.LastKnownFreeSaleSlots = 18;
        // One bag slot holding 999 materia, whose rule allows a single sale slot.
        // Counting raw quantity made that look like 50 stacks of trading buffer,
        // which filled the comfortable-stock target and stopped shopping entirely.
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 700, ItemName = "Savage Might Materia XII", TargetStackSize = 20,
            MaximumSaleSlots = 1, ListFromBags = true,
        });
        run.Game.OtherBagItems.Add(new(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
            5, 700, "Savage Might Materia XII", 999, false, 999));

        Assert.Equal(1, run.Controller.ResaleBagSlots);
        Assert.True(run.Controller.PurchaseCapacity > 0,
            "a single deep stack must not consume the whole shopping allowance");
    }

    [Fact]
    public void APurchaseIsCompletedByAnsweringTheConfirmationPrompt()
    {
        using var run = new Route();
        run.Game.NeedsPurchaseDialog = true;
        run.ReachPurchase();
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(ProcurementState.WaitingForPurchase, run.Controller.State);
        // Nothing has been bought yet; the board is waiting on the prompt.
        Assert.Empty(run.Ledger.Snapshot());

        run.Tick();
        Assert.Equal(1, run.Game.DialogsConfirmed);
        run.Tick();
        Assert.Equal(99u, Assert.Single(run.Ledger.Snapshot()).PendingQuantity);
    }

    [Fact]
    public void PriorityShoppingComparesTheWholeTripBeforeReturningForAnOrdinaryBuy()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Controller.RunNow();
        for (var i = 0; i < 300 && run.Game.Purchases == 0; i++) run.Tick(2);
        Assert.Equal(("Siren", 1u), run.Game.Searches.First());
        // Aether in full - the character's own data center, so no transfer - and
        // then across to Primal, rather than a crossing every second world.
        Assert.Equal(new[] { "/li Cactuar", "/li Adamantoise", "/li Faerie", "/li Gilgamesh",
            "/li Jenova", "/li Midgardsormr", "/li Sargatanas", "/li Behemoth", "/li Cactuar" }, run.Game.Commands);
        Assert.Equal(("Cactuar", 1u, 99u), Assert.Single(run.Game.Bought));
        run.Tick(2);
        Assert.Equal(99u, Assert.Single(run.Ledger.Snapshot()).PendingQuantity);
        Assert.Contains(run.Controller.RecentPrices, x => x.World == "Cactuar" && x.Decision.Contains("Save for comparison"));
        Assert.Contains(run.Log.Messages, x => x.StartsWith("SCOUT COMPARISON:"));
        Assert.DoesNotContain("/li mb", run.Game.Commands);
    }

    [Fact]
    public void PriorityBuyingUsesTheLiveStackQuantityInsteadOfAnOldCachedOrder()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20, 1000, 20, true, 1000)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 300 && run.Game.Purchases == 0; i++) run.Tick(2);
        Assert.Equal(("Cactuar", 1u, 20u), Assert.Single(run.Game.Bought));
        run.Tick(2);
        Assert.Equal(20u, Assert.Single(run.Ledger.Snapshot()).PendingQuantity);
    }

    [Fact]
    public void ExceptionalHomeBargainDoesNotPreemptScouting()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) =>
            [new(0, item, 10, 20, 100, 99, true, 495), new(1, item, 11, 21, 2000, 99, true, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 100 && run.Game.Purchases == 0; i++) run.Tick(2);
        Assert.Empty(run.Game.Bought);
        Assert.Contains(run.Game.Commands, c => c.StartsWith("/li "));
    }

    [Fact]
    public void PriorityCircuitReturnsToListAndResumesAcrossAllFourDataCenters()
    {
        using var run = new Route(priority: true);
        run.Config.Current.PriorityWorldsPerTrip = 4;
        run.Config.Current.PriorityMinutesPerTrip = 20;
        run.Game.AutomaticWorldArrival = true;
        run.Game.LiveProvider = (world, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        for (var trip = 0; trip < 8; trip++)
        {
            run.Repricing.IsActive = false;
            run.Controller.RunNow();
            for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
            Assert.Equal(ProcurementState.Completed, run.Controller.State);
            Assert.Equal("Siren", run.Game.World);
            Assert.Equal(trip + 1, run.Repricing.Starts);
        }
        var worlds = run.Game.Commands.Where(x => x != "/li Siren").Select(x => x[4..]).ToArray();
        Assert.Equal(new[] { "Cactuar", "Adamantoise", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Behemoth" }, worlds.Take(8));
        Assert.Equal(new[] {
            "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas",
            "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
            "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
            "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph"
        }.Order(), worlds.Order());
        Assert.Empty(run.Config.Current.PriorityNextWorld);
        Assert.Equal(0, run.Game.Purchases);
    }

    [Fact]
    public void PriorityChecksAllFlipsByDailySalesBeforeCategoryPreference()
    {
        using var run = new Route(priority: true);
        run.Config.Current.ProcurementRules.Clear();
        run.Config.Current.ProcurementRules.AddRange(new[] {
            new ProcurementRule { ItemId = 1, ItemName = "Dye", TourPriority = 1 },
            new ProcurementRule { ItemId = 2, ItemName = "Food", TourPriority = 0 },
            new ProcurementRule { ItemId = 3, ItemName = "Faster Dye", TourPriority = 1 },
            new ProcurementRule { ItemId = 4, ItemName = "Old materia", LiquidateOnly = true },
        });
        run.Game.DemandMarkets = Enumerable.Range(1, 4).Select(i => new ProcurementMarketItem((uint)i, "Item", [],
            [new(2000, (uint)(i * 100), false, DateTimeOffset.UtcNow)])).ToArray();
        run.Game.LiveProvider = (world, item) => [new(0, item, 10, 20, 2000, 99, false, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 100 && run.Game.Commands.Count == 0; i++) run.Tick(2);
        Assert.Equal(new[] { 3u, 2u, 1u }, run.Game.Searches.Select(x => x.Item));
        Assert.DoesNotContain(4u, run.Game.ScannedItemIds);
    }

    [Fact]
    public void HomeSearchPriorityUsesReportedVelocityForTheBuyableQuality()
    {
        using var run = new Route(priority: true);
        run.Config.Current.ProcurementRules.Add(new() { ItemId = 2, ItemName = "Fast NQ dye", TourPriority = 5 });
        run.Game.DemandMarkets = [
            new(1, "Popcorn", [], [new(2000, 700, true, DateTimeOffset.UtcNow)], NqSalesPerDay: 9000, HqSalesPerDay: 2),
            new(2, "Fast NQ dye", [], [new(2000, 70, false, DateTimeOffset.UtcNow)], NqSalesPerDay: 500),
        ];
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, item == 1, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 100 && run.Game.Commands.Count == 0; i++) run.Tick(2);
        Assert.Equal(new[] { 2u, 1u }, run.Game.Searches.Select(x => x.Item));
        Assert.Equal(2m, run.Controller.HomeSalesPerDay(1, true));
        Assert.Equal(500m, run.Controller.HomeSalesPerDay(2, false));
        Assert.Contains(run.Log.Messages, m => m.Contains("Home sales 500.0 units/day"));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(0, false)]
    public void LiquidPreferredMarginCanPurchaseWithinAvailableCapacity(int emptySlots, bool shouldBuy)
    {
        using var run = new Route(priority: true);
        run.Repricing.LastKnownFreeSaleSlots = emptySlots;
        run.Config.Current.ProcurementMinimumRoiPercent = 20;
        run.Config.Current.ProcurementFillRoiPercent = 10;
        // No spare-stack buffer, so "no empty sale slot" really is no capacity.
        run.Config.Current.ProcurementBagBufferStacks = 0;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20, 1600, 99, true, 7920)], // ~13% net ROI
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.False(run.Controller.IsActive);
        Assert.Equal(shouldBuy ? 1 : 0, run.Game.Purchases);
        if (shouldBuy)
        {
            Assert.Equal(ProcurementState.Completed, run.Controller.State);
            Assert.Equal(166_320u, run.Controller.Status.GilSpent);
            Assert.Single(run.Ledger.Snapshot());
            // The position, not a high-water mark: 166,320 gil of landed cost over
            // 99 units, and a floor that returns it plus the 10% bar after sale tax.
            var pricing = run.Config.Current.PerItemRules[1];
            Assert.Equal(1_680u, pricing.CostBasis);
            Assert.Equal(99u, pricing.CostBasisUnits);
            Assert.Equal(1_946u, pricing.AcquisitionFloor);
            // Nothing ratchets the configured margin, so a later cheaper buy is free
            // to lower the floor instead of being stranded above it.
            Assert.Equal(0m, pricing.MinimumMarginPercent);
            Assert.Contains(("Behemoth", 1u), run.Game.Searches);
        }
        else
        {
            // No capacity means no plan at all, not a plan that is merely unspent.
            Assert.Empty(run.Ledger.Snapshot());
            Assert.Empty(run.Game.Bought);
            Assert.Empty(run.Controller.Plan.Orders);
        }
    }

    [Fact]
    public void CongestedWorldsAreSkippedAndRetriedAtTheEndOfTheCircuit()
    {
        using var run = new Route(priority: true);
        run.Config.Current.PriorityWorldsPerTrip = 31;
        run.Config.Current.PriorityMinutesPerTrip = 480;
        run.Game.AutomaticWorldArrival = true;
        // The first two stops of the circuit are busy. Before, the two refusals in a
        // row read as a broken tour and ended it two worlds into a 31-world sweep.
        run.Game.CongestedWorlds.Add("Adamantoise");
        run.Game.CongestedWorlds.Add("Cactuar");
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 6000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.DoesNotContain(run.Log.Messages, m => m.Contains("consecutive worlds"));
        // Every world that could be reached was priced: 29 away worlds plus home.
        Assert.Equal(30, run.Game.Searches.Select(x => x.World).Distinct().Count());
        Assert.DoesNotContain(run.Game.Searches, x => x.World is "Adamantoise" or "Cactuar");
        var hops = run.Game.Commands.Where(c => c.StartsWith("/li ") && c != "/li mb").ToList();
        Assert.Equal(2, hops.Count(c => c == "/li Adamantoise"));
        Assert.Equal(2, hops.Count(c => c == "/li Cactuar"));
        // The retry happens behind the rest of the circuit, not on the spot.
        Assert.True(hops.LastIndexOf("/li Adamantoise") > hops.LastIndexOf("/li Seraph"));
        Assert.Contains(run.Log.Messages, m => m.Contains("Adamantoise moves to the end of the circuit"));
    }

    [Fact]
    public void AStoppedTourResumesPastTheWorldThatFailedRatherThanRepeatingIt()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = true;
        run.Game.BrokenBoardWorlds.Add("Adamantoise");
        run.Game.BrokenBoardWorlds.Add("Cactuar");
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Contains(run.Log.Messages, m => m.Contains("two consecutive worlds were reached"));
        // The cursor is past both, so the next trip does not restart into the wall.
        Assert.Equal("Faerie", run.Config.Current.PriorityNextWorld);
        run.Game.Commands.Clear();
        run.Repricing.IsActive = false;
        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal("/li Faerie", run.Game.Commands.First(c => c.StartsWith("/li ") && c != "/li mb"));
    }

    [Fact]
    public void AFullCachedCircuitSkipsTravelAndRereadsAfterKnowledgeExpires()
    {
        using var run = new Route(priority: true);
        run.Config.Current.PriorityWorldsPerTrip = 4;
        run.Game.AutomaticWorldArrival = true;
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        for (var trip = 0; trip < 8; trip++)
        {
            run.Repricing.IsActive = false;
            run.Controller.RunNow();
            for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
            Assert.Equal(ProcurementState.Completed, run.Controller.State);
        }
        Assert.Equal(31, run.Controller.ScoutKnowledgeCoverage.Known);
        run.Game.Commands.Clear();
        run.Game.Searches.Clear();
        run.Repricing.IsActive = false;
        run.Controller.RunNow();
        for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Empty(run.Game.Commands);
        Assert.DoesNotContain(run.Game.Searches, s => s.World != "Siren");
        Assert.DoesNotContain(run.Log.Messages, m => m.Contains("consecutive worlds"));
        run.Tick(25 * 3600);
        run.Repricing.IsActive = false;
        run.Controller.RunNow();
        for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(4, run.Game.Commands.Count(c => c != "/li Siren"));
        Assert.Contains(("Siren", 1u), run.Game.Searches);
        Assert.Contains(("Cactuar", 1u), run.Game.Searches);
    }

    [Fact]
    public void AChangedWinnerUpdatesRememberedPricesBeforeTheNextComparison()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" when run.Game.Commands.Count(c => c == "/li Cactuar") == 1 =>
                [new(0, item, 10, 20, 1000, 99, true, 4950)],
            "Cactuar" => [new(0, item, 10, 20, 1200, 99, true, 5940)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Empty(run.Game.Bought); // Respect the first comparison's 1000 ceiling.
        run.Repricing.IsActive = false;
        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(("Cactuar", 1u, 99u), Assert.Single(run.Game.Bought));
        Assert.Equal(124_740u, run.Controller.Status.GilSpent);
        Assert.Equal(3, run.Game.Commands.Count(c => c == "/li Cactuar")); // Scout, first check, updated winner.
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(90.0)]
    public void TheLowerFillMarginCannotBuyOpportunisticStockToOccupyASlot(double opportunisticCapPercent)
    {
        // Five empty sale slots, gil available, and a dye that clears the 10% fill
        // margin, the minimum profit per unit and the slot-value gate. It is still
        // not bought: the fill pass reaches preferred and high-liquidity stock only.
        // A generous opportunistic cap does not change that.
        using var run = new Route(priority: true);
        run.Config.Current.ProcurementMinimumRoiPercent = 20;
        run.Config.Current.ProcurementFillRoiPercent = 10;
        run.Config.Current.ProcurementRules[0].PreferredStock = true;
        run.Config.Current.OpportunisticPortfolioMaximumPercent = (decimal)opportunisticCapPercent;
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 2, ItemName = "Yellow Dye", TargetStackSize = 20, MaximumSaleSlots = 4, HuntOnTour = true,
        });
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.DemandMarkets = [
            new(1, "Popcorn", [], [new(2_000, 700, true, DateTimeOffset.UtcNow)]),
            new(2, "Yellow Dye", [], [new(1_500, 700, false, DateTimeOffset.UtcNow)]),
        ];
        // The curated flip is nowhere to be found away from home, so only the dye
        // could possibly fill the empty slots.
        run.Game.LiveProvider = (world, item) => world == "Siren"
            ? item == 1 ? [new(0, item, 11, 21, 2_000, 99, true, 0)] : [new(0, item, 11, 21, 900, 20, false, 0)]
            : item == 2 ? [new(0, item, 10, 20, 690, 20, false, 690)] : [];

        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);

        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Empty(run.Game.Bought);
        Assert.Contains(run.Log.Messages, m => m.Contains("STOCK TOP-UP") && m.Contains("stay empty"));
        Assert.DoesNotContain(run.Log.Messages, m => m.Contains("STOCK TOP-UP") && m.Contains("added"));
        // The cheap dye really was on the board; it was refused on portfolio
        // grounds, not because nothing was found.
        Assert.Contains(run.Controller.RecentPrices,
            p => p is { Item: "Yellow Dye", World: not "Siren", Lowest: 690 });
    }

    [Fact]
    public void ListedJunkPushesTheNextPurchasesBackTowardPreferredStock()
    {
        // Sixty opportunistic slots are already listed, so the portfolio is far
        // past its cap. The high-liquidity curated flip is still bought; nothing
        // opportunistic is added beside it.
        using var run = new Route(priority: true);
        run.Repricing.LastKnownFreeSaleSlots = 4;
        run.Repricing.ListedStock = [new(999, false, 600, 60)];
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 2, ItemName = "Yellow Dye", TargetStackSize = 20, MaximumSaleSlots = 4, HuntOnTour = true,
        });
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.DemandMarkets = [
            new(1, "Popcorn", [], [new(2_000, 700, true, DateTimeOffset.UtcNow)]),
            new(2, "Yellow Dye", [], [new(1_500, 700, false, DateTimeOffset.UtcNow)]),
        ];
        run.Game.LiveProvider = (world, item) => world == "Siren"
            ? item == 1 ? [new(0, item, 11, 21, 2_000, 99, true, 0)] : [new(0, item, 11, 21, 900, 20, false, 0)]
            : item == 1
                ? [new(0, item, 10, 20, 1_000, 99, true, 4_950)]
                : [new(0, item, 12, 22, 400, 20, false, 420)];

        run.Controller.RunNow();
        for (var i = 0; i < 600 && run.Controller.IsActive; i++) run.Tick(2);

        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        // Only the curated flip is bought, and it is no longer limited to a single
        // stack: a market this liquid is allowed the capital its demand supports.
        Assert.NotEmpty(run.Game.Bought);
        Assert.All(run.Game.Bought, b => Assert.Equal((1u, 99u), (b.Item, b.Quantity)));
        Assert.DoesNotContain(run.Game.Bought, b => b.Item == 2u);
        Assert.True(run.Controller.PortfolioSummary.OpportunisticSlots >
                    run.Controller.PortfolioSummary.OpportunisticCap);
        // The dye was profitable, fast moving and cheaper than the flip that was
        // bought. The listed backlog is what kept it out.
        Assert.Contains(run.Log.Messages,
            m => m.Contains("Yellow Dye") && m.Contains("opportunistic portfolio cap reached"));
        Assert.Contains(run.Log.Messages, m => m.Contains("Popcorn") && m.Contains("selected"));
    }

    [Fact]
    public void PurchaseIsCancelledIfExistingBagsCoverTheEmptySlots()
    {
        using var run = new Route(priority: true);
        run.Repricing.LastKnownFreeSaleSlots = 1;
        run.Config.Current.ProcurementMinimumRoiPercent = 20;
        run.Config.Current.ProcurementFillRoiPercent = 10;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        // ~13% net ROI: under the 14% high-volume bar the comparison pass applies,
        // over the 10% the top-up pass relaxes to. So it can only be a fill order.
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20, 1600, 99, true, 7920)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 600 && !run.Controller.Plan.Orders.Any(o => o.IsFillOrder); i++) run.Tick(2);
        Assert.Contains(run.Controller.Plan.Orders, o => o.IsFillOrder);
        run.Game.Inventory = 99;
        for (var i = 0; i < 300 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Empty(run.Game.Bought);
        Assert.Contains(run.Log.Messages, m => m.Contains("lower fill margin no longer applies"));
    }

    [Fact]
    public void MissingLiveHomePricesDoesNotSendThePriorityRouteShopping()
    {
        using var run = new Route(priority: true);
        run.Game.LiveProvider = (_, _) => [];
        run.Controller.RunNow();
        for (var i = 0; i < 100 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Empty(run.Game.Commands);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(1, run.Repricing.Starts);
    }

    [Fact]
    public void LimsaFallbackOnlyRunsWhenNoNearbyBoardCanBeUsed()
    {
        using var run = new Route();
        run.Config.Current.MarketBoardTravelCommand = "/li tp Limsa Lominsa Lower Decks";
        run.Game.ObjectDistance = 200;
        run.Begin(); run.Game.World = "Cactuar"; run.Tick(); run.Tick(9); run.Tick();
        Assert.Contains("/li tp Limsa Lominsa Lower Decks", run.Game.Commands);
        Assert.DoesNotContain("/li mb", run.Game.Commands);
    }

    [Fact]
    public void ExplicitServerPurchaseRejectionSkipsWithoutAnUnknownOutcomeStop()
    {
        using var run = new Route();
        run.ReachPurchase();
        run.Game.PurchaseError = 1234;
        run.Tick();
        Assert.False(run.Controller.RequiresManualRestart);
        Assert.Contains(run.Log.Messages, x => x.Contains("server rejected") && x.Contains("1234"));
        Assert.Empty(run.Ledger.Snapshot());
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void PriorityLoopAutomaticallyStartsAnotherTripAfterReturningToRetainers()
    {
        using var run = new Route(priority: true);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.PriorityWorldsPerTrip = 4;
        run.Config.Current.PriorityMinutesPerTrip = 20;
        run.Game.AutomaticWorldArrival = true;
        run.Game.LiveProvider = (world, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        for (var trip = 0; trip < 3; trip++)
        {
            run.Repricing.IsActive = false; // the simulated bell pass completed
            run.Tick(601);
            for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
            Assert.Equal(ProcurementState.Completed, run.Controller.State);
            Assert.Equal(trip + 1, run.Repricing.Starts);
            Assert.Equal("Siren", run.Game.World);
        }
        Assert.Equal(12, run.Game.Commands.Count(x => x != "/li Siren"));
        Assert.Contains("/li Behemoth", run.Game.Commands);
    }

    [Fact]
    public void PriorityTimeCheckpointPreservesTheNextItemOnTheSameWorld()
    {
        using var run = new Route(priority: true);
        run.Config.Current.PriorityWorldsPerTrip = 4;
        run.Config.Current.PriorityMinutesPerTrip = 20;
        run.Game.AutomaticWorldArrival = true;
        run.Config.Current.ProcurementRules[0].TourPriority = 0;
        run.Config.Current.ProcurementRules.Add(new() { ItemId = 2, ItemName = "Another flip" });
        run.Game.DemandMarkets = [
            new(1, "Popcorn", [], [new(2000, 100, true, DateTimeOffset.UtcNow)]),
            new(2, "Another flip", [], [new(2000, 100, false, DateTimeOffset.UtcNow)])];
        run.Game.LiveProvider = (world, item) => [new(0, item, 10, 20, 2000, 99, item == 1, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 100 && !run.Controller.RecentPrices.Any(x => x.World == "Adamantoise"); i++) run.Tick(2);
        run.Tick(1201);
        for (var i = 0; i < 100 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal("Adamantoise", run.Config.Current.PriorityNextWorld);
        Assert.Equal(2u, run.Config.Current.PriorityNextItem);
        run.Game.Searches.Clear(); run.Repricing.IsActive = false;
        run.Controller.RunNow();
        for (var i = 0; i < 100 && !run.Game.Searches.Any(x => x.World == "Adamantoise"); i++) run.Tick(2);
        Assert.Equal(2u, run.Game.Searches.First(x => x.World == "Adamantoise").Item);
    }

    [Fact]
    public void PriorityModeSearchesWithoutTravellingWhenTheBagTargetIsAlreadyMet()
    {
        using var run = new Route(priority: true);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ContinueShoppingWhenStocked = true;
        run.Game.Inventory = 20 * 99;
        run.Config.Current.ProcurementRules[0].MaximumSaleSlots = 20;
        run.Tick(); run.Tick();
        // The home world needs a full listings-and-history scan; the rest of the
        // region only needs the cached aggregate hints.
        Assert.Equal(1, run.Game.Scans);
        Assert.Equal(1, run.Game.HintRequests);
        Assert.Empty(run.Game.Commands);
        Assert.Equal(0, run.Game.Purchases);
        run.Tick(601); run.Tick();
        Assert.Equal(2, run.Game.Scans);
        Assert.Equal(2, run.Game.HintRequests);
    }

    [Theory]
    [InlineData("/li mb", "/li tp Limsa Lominsa Lower Decks")]
    [InlineData("/li my-board", "/li my-board")]
    public void ExistingDefaultTravelMigratesToLimsaAndCustomCommandsArePreserved(string before, string after)
    {
        var config = new Configuration { Version = 28, MarketBoardTravelCommand = before };
        config.Normalize();
        Assert.Equal(after, config.MarketBoardTravelCommand);
        Assert.True(config.PriorityShoppingEnabled);
        // Compare against a fresh config so this survives later migrations.
        Assert.Equal(new Configuration().Version, config.Version);
    }

    [Fact]
    public void ABetterNormalDealOnPrimalBeatsTheEarlierAetherOffer()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20, 1200, 99, true, 5940)],
            "Behemoth" => [new(0, item, 12, 22, 1000, 99, true, 4950)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 500 && run.Game.Purchases == 0; i++) run.Tick(2);
        Assert.Equal(("Behemoth", 1u, 99u), Assert.Single(run.Game.Bought));
        Assert.Contains(("Sargatanas", 1u), run.Game.Searches); // The trip finished Aether before buying
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void RevisitedDealThatBecameMoreExpensiveIsSkippedInsteadOfBuyingAboveTheComparedPrice()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20,
                run.Game.Commands.Count(c => c == "/li Cactuar") > 1 ? 1200u : 1000u, 99, true, 4950)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Empty(run.Game.Bought);
        Assert.Contains(run.Log.Messages, m => m.Contains("no live listing matched the plan"));
        Assert.Equal("Siren", run.Game.World);
    }

    [Fact]
    public void ExceptionalAwayDealWaitsForCircuitComparison()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.LiveProvider = (world, item) => world == "Siren"
            ? [new(0, item, 11, 21, 2000, 99, true, 0)]
            : [new(0, item, 10, 20, 100, 99, true, 495)];
        run.Controller.RunNow();
        for (var i = 0; i < 100 && run.Game.Purchases == 0; i++) run.Tick(2);
        Assert.Empty(run.Game.Bought);
        Assert.True(run.Game.Commands.Count > 1);
        Assert.DoesNotContain(run.Controller.RecentPrices, p => p.Decision.Contains("Buy exceptional"));
    }

    [Fact]
    public void LargeItemListGetsShortAwayScansOnEveryWorldOfTheTrip()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = true;
        run.Config.Current.ProcurementRules.Clear();
        run.Config.Current.ProcurementRules.AddRange(Enumerable.Range(1, 51)
            .Select(i => new ProcurementRule { ItemId = (uint)i, ItemName = $"Dye {i}" }));
        run.Game.DemandMarkets = Enumerable.Range(1, 51).Select(i => new ProcurementMarketItem((uint)i, $"Dye {i}", [],
            [new(2000, 100, false, DateTimeOffset.UtcNow)])).ToArray();
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, false, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 1000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(51, run.Game.Searches.Count(x => x.World == "Siren"));
        var away = run.Game.Searches.Where(x => x.World != "Siren").GroupBy(x => x.World).ToArray();
        Assert.Equal(8, away.Length);
        var perWorld = run.Config.Current.PriorityItemsPerWorld;
        Assert.All(away, world => Assert.Equal(perWorld, world.Count()));
        // Seven Aether worlds, then the first stop on Primal.
        Assert.Contains(away, w => w.Key == "Sargatanas");
        Assert.Contains(away, w => w.Key == "Behemoth");
    }

    [Fact]
    public void EveryAwayWorldPricesTheFoodAndPotionBlockNotJustTheFirstStop()
    {
        // The live 1.0.0.61 failure: with fifty-odd rules the scout window rotated
        // off the preferred stock, so the third world onwards priced only materia
        // and dye. The food block must appear on every stop of the circuit.
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = true;
        run.Config.Current.ProcurementRules.Clear();
        run.Config.Current.ProcurementRules.AddRange(Enumerable.Range(1, 6)
            .Select(i => new ProcurementRule
            {
                ItemId = (uint)i, ItemName = $"Gemdraught {i}", PreferredStock = true, HuntOnTour = true,
            }));
        run.Config.Current.ProcurementRules.AddRange(Enumerable.Range(7, 45)
            .Select(i => new ProcurementRule { ItemId = (uint)i, ItemName = $"Materia {i}" }));
        run.Game.DemandMarkets = Enumerable.Range(1, 51).Select(i => new ProcurementMarketItem((uint)i, $"Item {i}", [],
            [new(2000, 100, false, DateTimeOffset.UtcNow)])).ToArray();
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, false, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 1000 && run.Controller.IsActive; i++) run.Tick(2);

        var away = run.Game.Searches.Where(x => x.World != "Siren").GroupBy(x => x.World).ToArray();
        Assert.Equal(8, away.Length);
        Assert.All(away, world => Assert.All(Enumerable.Range(1, 6),
            food => Assert.Contains((uint)food, world.Select(x => x.Item))));
        // The rotation still runs: the secondary lines are not all the same eight.
        Assert.True(away.SelectMany(w => w.Select(x => x.Item)).Where(x => x > 6).Distinct().Count() > 8);
    }

    [Fact]
    public void ARareDyeFlaggedForSnipingIsPricedOnEveryWorldDespiteThinHomeSales()
    {
        // Jet Black and Pure White barely trade at home, so the volume ordering and
        // the rotation both bury them - yet a far world underpricing one is exactly
        // the deal worth catching. AlwaysScout puts them in the per-world block
        // without making them core portfolio stock.
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = true;
        run.Config.Current.PriorityWorldsPerTrip = 31;
        // The fake board answers far slower per search than the live client, so a
        // full-width stop would exhaust the trip clock around world 23. Four items
        // a stop still carries the whole two-item block plus rotation.
        run.Config.Current.PriorityMinutesPerTrip = 480;
        run.Config.Current.PriorityItemsPerWorld = 4;
        run.Config.Current.ProcurementRules.Clear();
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 1, ItemName = "Gemdraught", PreferredStock = true, HuntOnTour = true, AlwaysScout = true,
        });
        run.Config.Current.ProcurementRules.Add(new()
        {
            ItemId = 2, ItemName = "General-purpose Jet Black Dye", HuntOnTour = true, AlwaysScout = true,
            MinimumWeeklyUnitsSold = 5_000,
        });
        run.Config.Current.ProcurementRules.AddRange(Enumerable.Range(3, 40)
            .Select(i => new ProcurementRule { ItemId = (uint)i, ItemName = $"Materia {i}" }));
        run.Game.DemandMarkets = Enumerable.Range(1, 42).Select(i => new ProcurementMarketItem((uint)i, $"Item {i}", [],
            [new(2000, 100, false, DateTimeOffset.UtcNow)])).ToArray();
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, false, 0)];
        run.Controller.RunNow();
        for (var i = 0; i < 20_000 && run.Controller.IsActive; i++) run.Tick(2);

        var away = run.Game.Searches.Where(x => x.World != "Siren").GroupBy(x => x.World).ToArray();
        // Every away world, and both block items on each of them.
        Assert.Equal(31, away.Length);
        Assert.All(away, world =>
        {
            Assert.Contains(1u, world.Select(x => x.Item));
            Assert.Contains(2u, world.Select(x => x.Item));
        });
    }

    [Fact]
    public void UnansweredSearchRetriesWithinSecondsAndCanRecoverWithoutAPurchase()
    {
        using var run = new Route(priority: true);
        run.Game.ListingsReady = false;
        run.Controller.RunNow();
        for (var i = 0; i < 20 && run.Controller.State != ProcurementState.WaitingForStockHuntListings; i++) run.Tick(1);
        run.Tick(6);
        Assert.Contains(run.Log.Messages, m => m.Contains("Retrying the live search") && m.Contains("attempt 2/3"));
        Assert.Empty(run.Game.Bought);
        run.Game.ListingsReady = true;
        run.Tick(2);
        Assert.Contains(run.Controller.RecentPrices, p => p.World == "Siren" && p.Listings > 0);
        Assert.False(run.Controller.RequiresManualRestart);
    }

    [Fact]
    public void ExceptionalSpendingAndConfirmedCountCarryIntoTheComparedBuyingPass()
    {
        using var run = new Route(priority: true);
        run.Config.Current.ReinvestAvailableGil = false;
        run.Config.Current.ProcurementBudget = 106_000;
        run.Game.WeeklySalesQuantity = 2_000;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        // Eleven units, not one: a single-unit flip is no longer worth a whole
        // retainer slot however exceptional its percentage return looks.
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 11, 21, 2000, 99, true, 0)],
            "Cactuar" => [new(0, item, 10, 20, 100, 11, true, 55)],
            "Behemoth" => [new(0, item, 12, 22, 1000, 99, true, 4950)],
            _ => [],
        };
        run.Controller.RunNow();
        for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(2, run.Controller.Status.CurrentOrder);
        Assert.Equal(105_105u, run.Controller.Status.GilSpent);
        Assert.Equal(110, run.Game.Inventory);
        Assert.Equal(1_000_000u - 105_105u, run.Game.Gil);
    }

    [Fact]
    public void AnExpiredHomeQuoteIsRereadOnTheNextTripInsteadOfReusedThenRejected()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = true;
        run.Game.LiveProvider = (_, item) => [new(0, item, 11, 21, 2000, 99, true, 0)];
        for (var trip = 0; trip < 2; trip++)
        {
            run.Repricing.IsActive = false;
            run.Controller.RunNow();
            for (var i = 0; i < 500 && run.Controller.IsActive; i++) run.Tick(2);
            Assert.Equal(ProcurementState.Completed, run.Controller.State);
            run.Tick(1860);
        }
        Assert.Equal(2, run.Game.Searches.Count(s => s.World == "Siren"));
        Assert.Equal(16, run.Game.Searches.Count(s => s.World != "Siren"));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    internal sealed class Route : IDisposable
    {
        public Game Game { get; } = new();
        public ConfigurationService Config { get; } = new();
        public ProcurementLedger Ledger { get; } = new();
        public TestRetainerAutomation Repricing { get; } = new();
        public AutomationLog Log { get; } = new();
        public ProcurementController Controller { get; }
        public MarketDiscoveryService? Discovery { get; }
        private readonly Clock clock = new();
        public Route(bool priority = false, IMarketStatisticsProvider? statistics = null)
        {
            Config.Current.PriorityShoppingEnabled = priority;
            Game.UseLiveListings = priority;
            Config.Current.MarketBoardTravelCommand = "/li mb";
            Config.Current.AllowAutomaticPurchases = true;
            // Older regression cases exercise the optional fixed-buffer mode.
            Config.Current.ContinueShoppingWhenStocked = false;
            // These fixtures drive trip mechanics with a handful of slots and small
            // wallets, which is deliberately below the shipped "worth travelling
            // for" thresholds. The holds have their own tests; opt these out.
            Config.Current.ShoppingTripMinimumFreeSaleSlots = 0;
            Config.Current.ShoppingTripMinimumGil = 0;
            // Most cases here exercise trip mechanics, not circuit length, and the
            // shipped default now sweeps all 31 away worlds. Tests that assert a
            // full circuit set this back explicitly.
            Config.Current.PriorityWorldsPerTrip = 8;
            Config.Current.ProcurementRules.Add(new()
            {
                ItemId = 1, ItemName = "Popcorn", AllowHighQuality = true, RequireHighQuality = true, HuntOnTour = true,
            });
            if (statistics is not null)
                Discovery = new(statistics, Game.LookupItem, Config, Log, clock);
            Controller = new(Game, Game, Game, Game, Game, new ProcurementPlannerService(),
                Game, Game, Game, Game, Ledger, Repricing, Config, Log, clock, Discovery);
        }
        public void Tick(int seconds = 0) { clock.Advance(seconds); Game.Tick(); }
        public void Begin()
        {
            Controller.RunNow();
            Tick();
            Assert.Empty(Game.Commands); // UI close must settle before world travel.
            Tick(2);
            Assert.Equal("/li Cactuar", Game.Commands.Last());
        }
        public void ReachApproach()
        {
            Begin();
            Game.World = "Cactuar";
            Tick();
            Tick(9);
            Assert.Equal(ProcurementState.FindingMarketBoard, Controller.State);
        }
        public void ReachListings()
        {
            ReachApproach();
            Tick();
            Tick();
            Tick(4);
            Assert.Equal(ProcurementState.WaitingForListings, Controller.State);
        }
        public void ReachPurchase() { ReachListings(); Tick(); }
        public void Dispose() => Controller.Dispose();
    }

    internal sealed class Game : FakeRetainerService, IFramework, IPlayerState, ICommandManager, IRetainerListingService,
        IUniversalisService, IMarketPurchaseService, IVnavmeshService, ILifestreamService, ITaskbarAttentionService
    {
        public event Action<IFramework>? Update;
        public void Tick() => Update?.Invoke(this);
        public string World { get; set; } = "Siren";
        public bool IsLoaded { get; set; } = true;
        public TestWorldRef CurrentWorld => new(new(World));
        public TestWorldRef HomeWorld => new(new("Siren"));
        public bool BellOpen { get; set; } = true;
        public bool BoardOpen { get; set; }
        public bool OpenBoardOnLocalTravel { get; set; } = true;
        public bool InteractionSucceeds { get; set; } = true;
        public bool AutomaticWorldArrival { get; set; }
        // A congested world refuses the visit: travel ends with the character still
        // standing where it started, which is what the plugin has to recognise.
        public HashSet<string> CongestedWorlds { get; } = new(StringComparer.OrdinalIgnoreCase);
        // A world that can be reached but whose Market Board never opens.
        public HashSet<string> BrokenBoardWorlds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutomaticPurchaseConfirmation { get; set; }
        public bool ThrowAfterPurchaseSubmission { get; set; }
        public bool ListingsReady { get; set; } = true;
        public bool CheapOversizedHomeStack { get; set; }
        public bool MissingHomeListings { get; set; }
        public string BuyingWorld { get; set; } = "Cactuar";
        public uint WeeklySalesQuantity { get; set; } = 700;
        public int ExtraBuyListings { get; set; }
        public List<BagListingCandidate> OtherBagItems { get; } = [];
        public override bool IsRetainerListOpen => BellOpen;
        public override IReadOnlySet<ulong> OwnedRetainerIds { get; } = new HashSet<ulong>();
        public uint FreeInventorySlots { get; set; } = 50;
        public uint Gil { get; set; } = 1_000_000;
        public int Inventory { get; set; }
        public int Purchases { get; private set; }
        public uint PurchaseError { get; set; }
        public bool UseLiveListings { get; set; }
        public Func<string, uint, IReadOnlyList<LivePurchaseListing>>? LiveProvider { get; set; }
        public IReadOnlyList<ProcurementMarketItem>? DemandMarkets { get; set; }
        public List<(string World, uint Item)> Searches { get; } = [];
        public List<(string World, uint Item, uint Quantity)> Bought { get; } = [];
        public float ObjectDistance { get; set; } = 1;
        public uint BuyerTax { get; set; } = 4_950;
        public int Aborts { get; private set; }
        public List<string> Commands { get; } = [];
        public bool IsBusy { get; set; }
        public bool IsAvailable => true;
        public bool IsReady => true;
        public bool IsRunning => false;
        public bool IsMarketBoardOpen => BoardOpen;
        public bool ProcessCommand(string command)
        {
            Commands.Add(command);
            if (command.StartsWith("/li tp ")) { ObjectDistance = 1; return true; }
            if (command == "/li mb")
            {
                if (OpenBoardOnLocalTravel && !BrokenBoardWorlds.Contains(World)) BoardOpen = true;
            }
            else if (AutomaticWorldArrival && command.StartsWith("/li ") && !CongestedWorlds.Contains(command[4..]))
                World = command[4..];
            return true;
        }
        public bool ChangeWorld(string world) => true;
        public void Abort() { Aborts++; IsBusy = false; }
        public string ResolveDataCenter(string configured) => configured;

        // Stands in for the Lumina item sheet: only these ids exist, and only
        // these are tradable food or medicine.
        public Dictionary<uint, MarketItemFacts> ItemSheet { get; } = new()
        {
            [1] = new(1, "Popcorn", true, true, true, 999),
            [42] = new(42, "Discovered Stew", true, true, true, 99),
        };
        public MarketItemFacts? LookupItem(uint itemId) => ItemSheet.GetValueOrDefault(itemId);

        public int Scans { get; private set; }
        public int HintRequests { get; private set; }
        public bool HintsFail { get; set; }
        public List<uint> ScannedItemIds { get; } = [];

        // The cached aggregate endpoint names the cheapest world per item. It is a
        // routing hint only: nothing here can be bought without a live board read.
        public Task<IReadOnlyList<MarketPriceHint>> FetchPriceHintsAsync(IReadOnlyList<ProcurementRule> rules,
            string scope, CancellationToken cancellationToken)
        {
            HintRequests++;
            if (HintsFail)
                return Task.FromException<IReadOnlyList<MarketPriceHint>>(new HttpRequestException("aggregate down"));
            return Task.FromResult<IReadOnlyList<MarketPriceHint>>(rules
                .Where(x => x.ItemId != 0)
                .Select(x => new MarketPriceHint(x.ItemId, BuyingWorld, 1, 1_000, true, 14m))
                .ToArray());
        }
        public Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(IReadOnlyList<ProcurementRule> rules,
            string dataCenter, CancellationToken cancellationToken)
        {
            Scans++;
            ScannedItemIds.AddRange(rules.Select(x => x.ItemId));
            if (DemandMarkets is not null) return Task.FromResult(DemandMarkets);
            if (dataCenter == "Siren" && MissingHomeListings)
                return Task.FromResult<IReadOnlyList<ProcurementMarketItem>>([]);
            return Task.FromResult<IReadOnlyList<ProcurementMarketItem>>(
                [new(1, "Popcorn", dataCenter == "Siren"
                        ? [new(1, 11, 21, "Siren", 2, 2_000, 99, true)]
                        : Enumerable.Range(0, 1 + ExtraBuyListings)
                            .Select(i => new ProcurementMarketListing(1, (ulong)(10 + i), (ulong)(20 + i), BuyingWorld, 1, 1_000, 99, true)).ToArray(),
                    Enumerable.Range(1, 3).Select(i => new ProcurementSale(2_000, WeeklySalesQuantity / 3, true, DateTimeOffset.UtcNow.AddDays(-i))).ToArray())]);
        }
        public int GetInventoryCount(uint itemId, bool highQuality) => itemId == 1 && highQuality ? Inventory : 0;
        public override IReadOnlyList<BagListingCandidate> ReadBagListingCandidates() => Inventory <= 0 ? OtherBagItems :
            [..OtherBagItems, new(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1, 10, 1, "Popcorn", (uint)Inventory, true, 999)];
        public Vector3? FindNearest(string objectName) => Vector3.Zero;
        public float DistanceTo(Vector3 position) => ObjectDistance;
        public bool InteractNearest(string objectName, float maximumDistance = 5)
        {
            if (!InteractionSucceeds) return false;
            if (objectName == "Summoning Bell") BellOpen = true;
            else if (!BrokenBoardWorlds.Contains(World)) BoardOpen = true;
            return true;
        }
        public bool RequestListings(uint itemId) { Searches.Add((World, itemId)); return true; }
        public bool AreListingsReady(uint itemId) => ListingsReady;
        public void ResetListingRequest() { }
        public IReadOnlyList<LivePurchaseListing> ReadLiveListings(uint itemId) => LiveProvider?.Invoke(World, itemId) ?? (World switch
        {
            "Siren" when MissingHomeListings => [],
            "Siren" when CheapOversizedHomeStack => [new(0, 1, 11, 21, 900, 999, true, 0), new(1, 1, 12, 22, 2_000, 99, true, 0)],
            "Siren" => [new(0, 1, 11, 21, 2_000, 99, true, 0)],
            "Cactuar" => [new(0, 1, 10, 20, 1_000, 99, true, 4_950)],
            _ => [],
        });
        public bool TrySelectLiveListing(ProcurementOrder expected, IReadOnlySet<ulong> excludedRetainerIds,
            out LivePurchaseListing? listing)
        {
            if (UseLiveListings)
            {
                listing = ReadLiveListings(expected.ItemId).FirstOrDefault(x =>
                    x.ItemId == expected.ItemId && x.ListingId == expected.ListingId &&
                    x.PricePerUnit <= expected.MaximumAcceptableUnitPrice && x.Quantity <= expected.Quantity &&
                    x.IsHighQuality == expected.IsHighQuality && !excludedRetainerIds.Contains(x.RetainerId));
                return listing is not null;
            }
            listing = new(0, 1, expected.ListingId, expected.RetainerId, 1_000, 99, true, BuyerTax);
            return true;
        }
        public bool NeedsPurchaseDialog { get; set; }
        public int DialogsConfirmed { get; private set; }
        private LivePurchaseListing? awaitingConfirmation;
        public bool SubmitPurchase(LivePurchaseListing listing)
        {
            Purchases++;
            Bought.Add((World, listing.ItemId, listing.Quantity));
            if (ThrowAfterPurchaseSubmission) throw new InvalidOperationException("Submission response lost.");
            // The real board takes nothing until the yes/no prompt is answered.
            if (NeedsPurchaseDialog) { awaitingConfirmation = listing; return true; }
            if (AutomaticPurchaseConfirmation)
            {
                Inventory += (int)listing.Quantity;
                Gil -= listing.PricePerUnit * listing.Quantity + listing.TotalTax;
            }
            return true;
        }
        public bool TryConfirmPurchase(string itemName)
        {
            if (awaitingConfirmation is not { } pending) return false;
            DialogsConfirmed++;
            Inventory += (int)pending.Quantity;
            Gil -= pending.PricePerUnit * pending.Quantity + pending.TotalTax;
            awaitingConfirmation = null;
            return true;
        }
        public void CloseMarketBoard() => BoardOpen = false;
        public override void CloseRetainerList() => BellOpen = false;
        public bool MoveTo(Vector3 destination, float tolerance = 3) => true;
        public void Stop() { }
        public void StopFlashing() { }
        public void FlashUntilForeground() { }
    }
}
