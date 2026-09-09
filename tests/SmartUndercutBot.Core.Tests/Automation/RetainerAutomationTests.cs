using Dalamud.Plugin.Services;
using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests.Automation;

public sealed class RetainerAutomationTests
{
    [Fact]
    public void VisibleBellWaitsForRetainerDataInsteadOfHaltingWithZeroRetainers()
    {
        using var run = new Session();
        run.Game.RetainerDataReady = false;
        run.Bot.StartNow();
        run.Tick(10);
        Assert.Equal(AutomationState.WaitingForAvailableRetainers, run.Bot.State);
        Assert.False(run.Bot.RequiresManualRestart);
        run.Game.RetainerDataReady = true;
        run.Tick();
        run.CompletePass();
        Assert.Equal(20, run.Bot.LastKnownFreeSaleSlots);
    }

    [Fact]
    public void FailedSelectionRecoversAndRepeatsForThreeSimulatedDays()
    {
        using var run = new Session();
        run.Game.FailNextSelection = true;
        run.Bot.StartNow();
        run.Tick();
        run.Tick(); // ready list must remain stable before selection
        Assert.Equal(AutomationState.RecoveringRetainerInterface, run.Bot.State);
        run.Tick();
        Assert.Equal(AutomationState.WaitingToRetryRetainers, run.Bot.State);
        Assert.Null(run.Bot.LastKnownFreeSaleSlots);
        for (var i = 0; i < 3 * 24 * 60; i++) run.Tick(60);
        Assert.False(run.Bot.RequiresManualRestart);
        Assert.True(run.Game.CompletedPasses > 150);
        Assert.Equal(20, run.Bot.LastKnownFreeSaleSlots);
    }

    [Fact]
    public void BagFillDoesNotPostponeTheNextFullCheck()
    {
        using var run = new Session();
        run.Bot.StartNow();
        run.CompletePass();
        var next = run.Bot.Status.NextActionAt;
        Assert.NotNull(next);
        run.Tick(120);
        run.Bot.StartBagListingNow();
        run.CompletePass();
        Assert.Equal(next, run.Bot.Status.NextActionAt);
        run.Tick(300);
        Assert.True(run.Bot.IsActive);
    }

    [Fact]
    public void ABagListingPassIsNotPlottedAsAFullBellWealthPoint()
    {
        // A fill-only pass never reads the existing listings, so its valuation is
        // retainer gil and nothing else. Plotting it drew the net worth graph as a
        // cliff down to bare gil and straight back up on the next real pass.
        using var run = new Session();
        run.Bot.StartNow();
        run.CompletePass();
        var repriced = run.Bot.PortfolioSnapshot();
        Assert.True(repriced.IsFullBellRun);
        Assert.NotNull(WealthHistory.FromValuation(repriced, DateTimeOffset.UtcNow));

        run.Tick(120);
        run.Bot.StartBagListingNow();
        run.CompletePass();
        var filled = run.Bot.PortfolioSnapshot();
        Assert.False(filled.IsFullBellRun);
        Assert.Null(WealthHistory.FromValuation(filled, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StopDuringRecoveryNeverRetries()
    {
        using var run = new Session();
        run.Game.FailNextSelection = true;
        run.Bot.StartNow(); run.Tick(); run.Tick();
        run.Bot.Halt();
        for (var i = 0; i < 100; i++) run.Tick(600);
        Assert.Equal(AutomationState.Halted, run.Bot.State);
        Assert.True(run.Bot.RequiresManualRestart);
        Assert.Equal(0, run.Game.CompletedPasses);
    }

    [Fact]
    public void MissingBellSchedulesAutomaticRecoveryAfterTheDeadline()
    {
        using var run = new Session();
        run.Game.FailNextSelection = true;
        run.Bot.StartNow(); run.Tick(); run.Tick();
        run.Game.BellOpen = false;
        run.Tick(61);
        Assert.Equal(AutomationState.WaitingToRetryRetainers, run.Bot.State);
        Assert.False(run.Bot.RequiresManualRestart);
        Assert.Contains("60 seconds", run.Bot.Status.Detail);
        Assert.Contains("retries automatically", run.Bot.Status.Detail);
    }

    [Fact]
    public void UnverifiedListingIsNeverRetriedByMenuRecovery()
    {
        using var run = new Session();
        run.Game.UnverifiedListing = true;
        run.Bot.StartNow();
        for (var i = 0; i < 100 && !run.Bot.RequiresManualRestart; i++) run.Tick();
        Assert.True(run.Bot.RequiresManualRestart);
        var submissions = run.Game.ListingSubmissions;
        for (var i = 0; i < 100; i++) run.Tick(600);
        Assert.Equal(1, submissions);
        Assert.Equal(submissions, run.Game.ListingSubmissions);
    }

    [Fact]
    public void AListingThatAlwaysFailsIsDroppedInsteadOfBeingReopenedForever()
    {
        using var run = new Session();
        // Adjust Price never opens for this row, which sends the controller through
        // interface recovery. Recovery replays the whole pass from the bell, so
        // without a limit it reopens this same listing every retry, indefinitely.
        run.Game.AdjustPriceWorks = false;
        run.Game.Listings.Add(new(1, "Test Retainer", 3, 100, "Popoto Potage", 99, 5_000, true));
        run.Bot.StartNow();

        for (var i = 0; i < 4_000 && run.Bot.State != AutomationState.WaitingForScheduledRun; i++)
            run.Tick(2);

        // It gives up on the row and finishes the pass rather than cycling forever.
        Assert.Equal(AutomationState.WaitingForScheduledRun, run.Bot.State);
        Assert.False(run.Bot.RequiresManualRestart);
        Assert.InRange(run.Game.ContextMenuOpens, 1, 6);
    }

    [Fact]
    public void AHealthyListingIsStillOpenedNormally()
    {
        using var run = new Session();
        run.Game.Listings.Add(new(1, "Test Retainer", 3, 100, "Popoto Potage", 99, 5_000, true));
        run.Bot.StartNow();
        for (var i = 0; i < 200 && run.Game.ContextMenuOpens == 0; i++)
            run.Tick(2);
        Assert.Equal(1, run.Game.ContextMenuOpens);
    }

    [Fact]
    public void UnmappedWrappedDyeUsesTheReplyItemAndWritesOnlyTheMatchingSlot()
    {
        using var run = new Session();
        run.Game.LiveRepricing = true;
        run.Game.Listings.AddRange([
            new(1, "Test Retainer", 2, 41760, "Savage Might Materia XI", 1, 9000, false),
            new(1, "Test Retainer", 7, 13721, "General-purpose Metallic Sky Blue Dye", 5, 6598, false),
        ]);
        // Visible order is the reverse of inventory order; the name becomes
        // available only after Compare Prices, with the wrap in the screenshot.
        run.Bot.StartNow();
        run.CompletePass();
        Assert.Equal(new[] { 0u, 0u }, run.Game.RequestedItems);
        Assert.Equal(new[] { (13721u, (short)7, 6596u), (41760u, (short)2, 8998u) }, run.Game.Commits);
        Assert.Equal(6596u, run.Game.Listings.Single(x => x.ItemId == 13721).CurrentPrice);
    }

    [Fact]
    public void DroppedSelectionIsRetriedOnceAfterTheFreshBellSettles()
    {
        using var run = new Session();
        run.Game.DropSelections = 1;
        run.Bot.StartNow();
        run.Tick();
        Assert.Equal(0, run.Game.Selections);
        run.Tick();
        Assert.Equal(1, run.Game.Selections);
        run.Tick(4);
        Assert.Equal(1, run.Game.Selections);
        run.Tick(1);
        Assert.Equal(2, run.Game.Selections);
        run.CompletePass();
        Assert.Equal(20, run.Bot.LastKnownFreeSaleSlots);
        Assert.False(run.Bot.RequiresManualRestart);
    }

    [Fact]
    public void AMenuThatDisappearsAfterSelectionReopensTheNearbyBellAndFinishes()
    {
        using var run = new Session();
        run.Game.DropSelections = 1;
        run.Game.CloseBellWhenSelectionDrops = true;
        run.Game.CanReopenBell = true;
        run.Bot.StartNow(); run.Tick(); run.Tick();
        run.Tick(21);
        Assert.Equal(AutomationState.RecoveringRetainerInterface, run.Bot.State);
        run.Tick();
        Assert.Equal(1, run.Game.BellInteractions);
        Assert.True(run.Game.BellOpen);
        for (var i = 0; i < 100 && run.Bot.State != AutomationState.WaitingForScheduledRun; i++) run.Tick();
        Assert.Equal(AutomationState.WaitingForScheduledRun, run.Bot.State);
        Assert.False(run.Bot.RequiresManualRestart);
        Assert.Equal(20, run.Bot.LastKnownFreeSaleSlots);
        Assert.Equal(2, run.Game.Selections);
    }

    [Fact]
    public void RecoveryAcceptsAReadyBellEvenWhenTheNextFrameArrivesAfterTheDeadline()
    {
        using var run = new Session();
        run.Game.FailNextSelection = true;
        run.Bot.StartNow(); run.Tick(); run.Tick();
        run.Tick(61);
        Assert.Equal(AutomationState.WaitingToRetryRetainers, run.Bot.State);
        Assert.True(run.Game.BellOpen);
        Assert.Equal(0, run.Game.BellCloses);
    }

    [Fact]
    public void ProlongedMenuOutageBacksOffThenRecoversWhenTheBellReturns()
    {
        using var run = new Session();
        run.Game.DropSelections = 1;
        run.Game.CloseBellWhenSelectionDrops = true;
        run.Bot.StartNow(); run.Tick(); run.Tick(); run.Tick(21);
        // Six hours with no usable bell. Recovery must not click every frame or
        // permanently latch after its fourth attempt.
        for (var i = 0; i < 6 * 60 * 60; i++) run.Tick(1);
        Assert.False(run.Bot.RequiresManualRestart);
        Assert.Null(run.Bot.LastKnownFreeSaleSlots);
        Assert.InRange(run.Game.BellInteractions, 12, 900);
        var before = run.Game.BellInteractions;
        run.Game.CanReopenBell = true;
        for (var i = 0; i < 200 && run.Bot.State != AutomationState.WaitingForScheduledRun; i++) run.Tick(5);
        Assert.Equal(AutomationState.WaitingForScheduledRun, run.Bot.State);
        Assert.Equal(before + 1, run.Game.BellInteractions);
        Assert.Equal(20, run.Bot.LastKnownFreeSaleSlots);
        Assert.Equal(0, run.Game.ListingSubmissions);
    }

    [Fact]
    public void AnUninitialisedVisibleBellIsReopenedInsteadOfSelected()
    {
        using var run = new Session();
        run.Game.BellReady = false;
        run.Game.CanReopenBell = true;
        run.Bot.StartNow(); run.Tick(31);
        Assert.Equal(AutomationState.RecoveringRetainerInterface, run.Bot.State);
        run.Tick(61);
        Assert.Equal(1, run.Game.BellCloses);
        Assert.Equal(0, run.Game.Selections);
        for (var i = 0; i < 100 && run.Bot.State != AutomationState.WaitingForScheduledRun; i++) run.Tick(2);
        Assert.Equal(AutomationState.WaitingForScheduledRun, run.Bot.State);
        Assert.Equal(1, run.Game.BellInteractions);
    }

    [Fact]
    public void DisablingAutomationDuringRecoveryPreventsAnotherBellInteraction()
    {
        using var run = new Session();
        run.Game.DropSelections = 1;
        run.Game.CloseBellWhenSelectionDrops = true;
        run.Bot.StartNow(); run.Tick(); run.Tick(); run.Tick(21);
        run.Config.Current.DisableStockAutomation();
        run.Game.CanReopenBell = true;
        for (var i = 0; i < 100; i++) run.Tick(30);
        Assert.Equal(0, run.Game.BellInteractions);
        Assert.Equal(AutomationState.Completed, run.Bot.State);
    }

    private sealed class Session : IDisposable
    {
        public Game Game { get; } = new();
        public AutomationController Bot { get; }
        public ConfigurationService Config { get; } = new();
        private readonly Clock clock = new();
        public Session()
        {
            var config = Config;
            config.Current.EnableStockAutomation();
            config.Current.RepeatMinimumMinutes = config.Current.RepeatMaximumMinutes = 5;
            Bot = new(Game, Game, Game, new PricingStrategyService(), new PortfolioValuationService(),
                config, new ProcurementLedger(), new AutomationLog(), clock);
        }
        public void Tick(int seconds = 2) { clock.Advance(seconds); Game.Tick(); }
        public void CompletePass()
        {
            for (var i = 0; i < 100 && Bot.State != AutomationState.WaitingForScheduledRun; i++) Tick();
            Assert.Equal(AutomationState.WaitingForScheduledRun, Bot.State);
        }
        public void Dispose() => Bot.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    private sealed class Game : FakeRetainerService, IFramework, IMarketDataService
    {
        public event Action<IFramework>? Update;
        public void Tick() => Update?.Invoke(this);
        public bool BellOpen = true;
        private bool menuOpen;
        private bool sellOpen;
        public bool FailNextSelection;
        public int DropSelections;
        public bool CloseBellWhenSelectionDrops;
        public bool BellReady = true;
        public bool CanReopenBell;
        public int Selections;
        public int BellInteractions;
        public int BellCloses;
        public bool RetainerDataReady = true;
        public bool UnverifiedListing;
        public int CompletedPasses;
        public int ListingSubmissions;
        public override bool IsRetainerListOpen => BellOpen;
        public override bool IsRetainerListReady => BellOpen && BellReady && RetainerDataReady;
        public override bool TryReopenRetainerList()
        {
            BellInteractions++;
            if (!CanReopenBell) return false;
            BellOpen = BellReady = true;
            return true;
        }
        public override void CloseRetainerList() { BellCloses++; BellOpen = false; }
        public override bool IsRetainerMenuOpen => menuOpen;
        public override bool IsSellListOpen => sellOpen;
        public override IReadOnlyList<int> AvailableRetainerIndices => RetainerDataReady ? [0] : [];
        public override bool SelectRetainer(int index)
        {
            Selections++;
            if (FailNextSelection) { FailNextSelection = false; return false; }
            if (DropSelections > 0)
            {
                DropSelections--;
                if (CloseBellWhenSelectionDrops) BellOpen = false;
                return true;
            }
            BellOpen = false; menuOpen = true; return true;
        }
        public override bool SelectSellItems() { menuOpen = false; sellOpen = true; return true; }
        public override bool CloseSellList() { sellOpen = false; menuOpen = true; return true; }
        public override bool CloseRetainerMenu() { menuOpen = false; BellOpen = true; CompletedPasses++; return true; }
        public override bool TryAutoListPurchase(ProcurementLedger ledger, out PendingAutoListing? pending)
        {
            pending = UnverifiedListing ? new(1, "Test Dye", 1, 1000, 0, false) : null;
            if (pending is not null) ListingSubmissions++;
            return pending is not null;
        }
        public override bool VerifyAutoListing(PendingAutoListing pending) => false;
        public List<RetainerListing> Listings = [];
        public bool AdjustPriceWorks = true;
        public int ContextMenuOpens;
        private bool contextOpen;
        public bool LiveRepricing;
        private bool editorOpen;
        private int visibleIndex;
        private bool pricesReturned;
        private uint requestedItem;
        private TaskCompletionSource<MarketSnapshot>? request;
        public List<uint> RequestedItems = [];
        public List<(uint Item, short Slot, uint Price)> Commits = [];
        private RetainerListing Editor => Listings.AsEnumerable().Reverse().ElementAt(visibleIndex);
        public override bool IsPriceEditorOpen => editorOpen;
        public override bool IsContextMenuOpen => contextOpen;
        public override IReadOnlyList<RetainerListing> ReadCurrentListings() => Listings;
        public override bool OpenListingContextMenu(int index)
        {
            ContextMenuOpens++;
            visibleIndex = index;
            contextOpen = true;
            return true;
        }
        public override bool SelectAdjustPrice()
        {
            contextOpen = false;
            editorOpen = LiveRepricing && AdjustPriceWorks;
            pricesReturned = false;
            return AdjustPriceWorks;
        }
        public override void CloseContextMenu() => contextOpen = false;
        public override void CancelPriceEditor() => editorOpen = false;
        public override bool TryResolveOpenPriceEditor(uint id, IReadOnlySet<short> slots, out RetainerListing? listing)
        {
            listing = pricesReturned ? RetainerEditorMatcher.Resolve(Listings, slots, id,
                Editor.ItemName.Replace("Blue Dye", "Blue\nDye"), Editor.Quantity, Editor.CurrentPrice, Editor.IsHighQuality) : null;
            return listing is not null;
        }
        public override bool IsOpenPriceEditorFor(RetainerListing listing, bool requirePriceMatch) =>
            editorOpen && listing.ItemId == Editor.ItemId && listing.Slot == Editor.Slot &&
            (!requirePriceMatch || listing.CurrentPrice == Editor.CurrentPrice);
        public override bool RequestComparePrices()
        {
            pricesReturned = true;
            if (requestedItem != 0 && requestedItem != Editor.ItemId)
                request!.SetException(new TimeoutException("Wrong provisional inventory item filtered out the actual reply."));
            else request!.SetResult(new(Editor.ItemId, DateTimeOffset.UtcNow,
                [new(Editor.CurrentPrice - 1, 2, false, "Competitor", 99)], Editor.CurrentPrice));
            return true;
        }
        public override PriceUpdateResult CommitPrice(RetainerListing expected, uint price)
        {
            Assert.True(IsOpenPriceEditorFor(expected, true));
            Commits.Add((expected.ItemId, expected.Slot, price));
            var index = Listings.FindIndex(x => x.Slot == expected.Slot);
            Listings[index] = expected with { CurrentPrice = price };
            editorOpen = false;
            return new(true, "Simulated server accepted price.");
        }
        public override bool TryReadListing(short slot, out RetainerListing? listing)
        { listing = Listings.FirstOrDefault(x => x.Slot == slot); return listing is not null; }
        public Task<MarketSnapshot> GetSnapshotAsync(uint id, CancellationToken token)
        {
            RequestedItems.Add(id);
            requestedItem = id;
            request = new();
            return request.Task;
        }
        public void ClearCache() { }
    }
}
