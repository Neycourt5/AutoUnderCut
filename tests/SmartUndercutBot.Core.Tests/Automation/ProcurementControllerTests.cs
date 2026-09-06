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
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void LiveAllWorldTourBuildsPlanAndCompletes(bool cheapOversizedHomeStack, int expectedPurchases)
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
        Assert.Contains("/li Ravana", run.Game.Commands);
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
        run.Tick(11);
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
        run.ReachPurchase();
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
        run.Repricing.LastKnownFreeSaleSlots = 0;
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
        run.Repricing.LastKnownFreeSaleSlots = 0;
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

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    private sealed class Route : IDisposable
    {
        public Game Game { get; } = new();
        public ConfigurationService Config { get; } = new();
        public ProcurementLedger Ledger { get; } = new();
        public AutomationController Repricing { get; } = new();
        public AutomationLog Log { get; } = new();
        public ProcurementController Controller { get; }
        private readonly Clock clock = new();
        public Route()
        {
            Config.Current.AllowAutomaticPurchases = true;
            Config.Current.ProcurementRules.Add(new() { ItemId = 1, AllowHighQuality = true, RequireHighQuality = true });
            Controller = new(Game, Game, Game, Game, Game, new ProcurementPlannerService(),
                Game, Game, Game, Game, Ledger, Repricing, Config, Log, clock);
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
            Tick();
            Tick(13);
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

    private sealed class Game : IFramework, IPlayerState, ICommandManager, IRetainerListingService,
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
        public bool AutomaticPurchaseConfirmation { get; set; }
        public bool ThrowAfterPurchaseSubmission { get; set; }
        public bool ListingsReady { get; set; } = true;
        public bool CheapOversizedHomeStack { get; set; }
        public bool MissingHomeListings { get; set; }
        public bool IsRetainerListOpen => BellOpen;
        public IReadOnlySet<ulong> OwnedRetainerIds { get; } = new HashSet<ulong>();
        public uint FreeInventorySlots { get; set; } = 50;
        public uint Gil { get; set; } = 1_000_000;
        public int Inventory { get; set; }
        public int Purchases { get; private set; }
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
            if (command == "/li mb" && OpenBoardOnLocalTravel) BoardOpen = true;
            else if (AutomaticWorldArrival && command.StartsWith("/li ")) World = command[4..];
            return true;
        }
        public bool ChangeWorld(string world) => true;
        public void Abort() { Aborts++; IsBusy = false; }
        public string ResolveDataCenter(string configured) => configured;
        public int Scans { get; private set; }
        public List<uint> ScannedItemIds { get; } = [];
        public Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(IReadOnlyList<ProcurementRule> rules,
            string dataCenter, CancellationToken cancellationToken)
        {
            Scans++;
            ScannedItemIds.AddRange(rules.Select(x => x.ItemId));
            if (dataCenter == "Siren" && MissingHomeListings)
                return Task.FromResult<IReadOnlyList<ProcurementMarketItem>>([]);
            return Task.FromResult<IReadOnlyList<ProcurementMarketItem>>(
                [new(1, "Popcorn", dataCenter == "Siren"
                        ? [new(1, 11, 21, "Siren", 2, 2_000, 99, true)]
                        : [new(1, 10, 20, "Cactuar", 1, 1_000, 99, true)],
                    [new(2_000, 99, true, DateTimeOffset.UtcNow)])]);
        }
        public int GetInventoryCount(uint itemId, bool highQuality) => Inventory;
        public Vector3? FindNearest(string objectName) => Vector3.Zero;
        public float DistanceTo(Vector3 position) => 1;
        public bool InteractNearest(string objectName, float maximumDistance = 5)
        {
            if (!InteractionSucceeds) return false;
            if (objectName == "Summoning Bell") BellOpen = true;
            else BoardOpen = true;
            return true;
        }
        public bool RequestListings(uint itemId) => true;
        public bool AreListingsReady(uint itemId) => ListingsReady;
        public void ResetListingRequest() { }
        public IReadOnlyList<LivePurchaseListing> ReadLiveListings(uint itemId) => World switch
        {
            "Siren" when MissingHomeListings => [],
            "Siren" when CheapOversizedHomeStack => [new(0, 1, 11, 21, 900, 999, true, 0), new(1, 1, 12, 22, 2_000, 99, true, 0)],
            "Siren" => [new(0, 1, 11, 21, 2_000, 99, true, 0)],
            "Cactuar" => [new(0, 1, 10, 20, 1_000, 99, true, 4_950)],
            _ => [],
        };
        public bool TrySelectLiveListing(ProcurementOrder expected, IReadOnlySet<ulong> excludedRetainerIds,
            out LivePurchaseListing? listing)
        {
            listing = new(0, 1, 10, 20, 1_000, 99, true, BuyerTax);
            return true;
        }
        public bool SubmitPurchase(LivePurchaseListing listing)
        {
            Purchases++;
            if (ThrowAfterPurchaseSubmission) throw new InvalidOperationException("Submission response lost.");
            if (AutomaticPurchaseConfirmation) Inventory += (int)listing.Quantity;
            return true;
        }
        public void CloseMarketBoard() => BoardOpen = false;
        public void CloseRetainerList() => BellOpen = false;
        public bool MoveTo(Vector3 destination, float tolerance = 3) => true;
        public void Stop() { }
        public void StopFlashing() { }
        public void FlashUntilForeground() { }
    }
}
