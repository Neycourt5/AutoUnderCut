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
    public void MenuThatCannotCloseStopsAfterRecoveryDeadline()
    {
        using var run = new Session();
        run.Game.FailNextSelection = true;
        run.Bot.StartNow(); run.Tick();
        run.Game.BellOpen = false;
        run.Tick(61);
        Assert.Equal(AutomationState.Halted, run.Bot.State);
        Assert.True(run.Bot.RequiresManualRestart);
        Assert.Contains("60 seconds", run.Bot.Status.Detail);
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

    private sealed class Session : IDisposable
    {
        public Game Game { get; } = new();
        public AutomationController Bot { get; }
        private readonly Clock clock = new();
        public Session()
        {
            var config = new ConfigurationService();
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
        public bool RetainerDataReady = true;
        public bool UnverifiedListing;
        public int CompletedPasses;
        public int ListingSubmissions;
        public override bool IsRetainerListOpen => BellOpen;
        public override bool IsRetainerMenuOpen => menuOpen;
        public override bool IsSellListOpen => sellOpen;
        public override IReadOnlyList<int> AvailableRetainerIndices => RetainerDataReady ? [0] : [];
        public override bool SelectRetainer(int index)
        {
            if (FailNextSelection) { FailNextSelection = false; return false; }
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
        public Task<MarketSnapshot> GetSnapshotAsync(uint id, CancellationToken token) => throw new NotSupportedException();
        public void ClearCache() { }
    }
}
