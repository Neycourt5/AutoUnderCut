using System.Numerics;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public enum AutomationState
{
    Idle,
    WaitingBeforeRetainerSelection,
    WaitingForRetainerMenu,
    WaitingBeforeOpeningSellList,
    WaitingForSellList,
    WaitingForStableInterface,
    ReadingListings,
    WaitingBeforeOpeningListing,
    WaitingForContextMenu,
    WaitingBeforeOpeningPriceEditor,
    WaitingForPriceEditor,
    RequestingMarketData,
    EvaluatingPrice,
    WaitingBeforeCommit,
    CommittingPrice,
    WaitingAfterCommit,
    WaitingBeforeClosingSellList,
    WaitingForRetainerMenuAfterSellList,
    WaitingBeforeClosingRetainer,
    WaitingForRetainerList,
    Completed,
    Halted,
    Faulted,
}

public sealed record AutomationQueueEntry(
    int Index,
    RetainerListing Listing,
    string Status,
    PriceDecision? Decision = null);

public sealed record AutomationStatus(
    AutomationState State,
    string Detail,
    int CurrentIndex,
    int TotalListings,
    int UpdatesSubmitted,
    DateTimeOffset? NextActionAt,
    int CurrentRetainer,
    int TotalRetainers);

public sealed class AutomationController : IDisposable
{
    private readonly IFramework framework;
    private readonly IRetainerListingService retainerListings;
    private readonly IMarketDataService marketData;
    private readonly IPricingStrategyService pricing;
    private readonly ConfigurationService configuration;
    private readonly AutomationLog log;
    private readonly List<AutomationQueueEntry> queue = [];

    private CancellationTokenSource? sessionCancellation;
    private Task<MarketSnapshot>? marketTask;
    private MarketSnapshot? currentMarket;
    private PriceDecision? currentDecision;
    private Vector3 sessionPosition;
    private DateTimeOffset nextActionAt;
    private DateTimeOffset stateDeadline;
    private DateTimeOffset verificationDeadline;
    private int currentIndex;
    private int updatesSubmitted;
    private int retainerIndex;
    private int retainerCount;
    private bool bellSession;
    private bool handledBell;
    private bool handledSellList;
    private string detail = "Open a summoning bell to begin.";

    public AutomationController(
        IFramework framework,
        IRetainerListingService retainerListings,
        IMarketDataService marketData,
        IPricingStrategyService pricing,
        ConfigurationService configuration,
        AutomationLog log)
    {
        this.framework = framework;
        this.retainerListings = retainerListings;
        this.marketData = marketData;
        this.pricing = pricing;
        this.configuration = configuration;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public event Action? RetainerInterfaceOpened;
    public AutomationState State { get; private set; } = AutomationState.Idle;

    public AutomationStatus Status => new(
        State,
        detail,
        currentIndex,
        queue.Count,
        updatesSubmitted,
        IsDelayState(State) ? nextActionAt : null,
        retainerCount == 0 ? 0 : retainerIndex + 1,
        retainerCount);

    public IReadOnlyList<AutomationQueueEntry> QueueSnapshot() => queue.ToArray();

    public void StartNow()
    {
        if (retainerListings.IsRetainerListOpen)
            BeginBellSession();
        else if (retainerListings.IsSellListOpen)
            BeginCurrentRetainerSession();
        else
            Halt("Cannot start: open the summoning-bell retainer list or a retainer sell list first.");
    }

    public void Halt(string reason = "Stopped by user.")
    {
        sessionCancellation?.Cancel();
        marketTask = null;
        currentMarket = null;
        currentDecision = null;
        retainerListings.CloseComparePrices();
        if (retainerListings.IsPriceEditorOpen)
            retainerListings.CancelPriceEditor();
        State = AutomationState.Halted;
        detail = reason;
        log.Add(AutomationLogLevel.Warning, $"Automation halted: {reason}");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            sessionCancellation?.Cancel();
            State = AutomationState.Faulted;
            detail = ex.Message;
            log.Add(AutomationLogLevel.Error, $"Automation faulted: {ex}");
        }
    }

    private void Tick()
    {
        TrackInterfaceLifecycle();

        if (State is AutomationState.Idle or AutomationState.Completed or AutomationState.Halted or AutomationState.Faulted)
        {
            TryAutoStart();
            return;
        }

        var safety = retainerListings.CheckSafety(sessionPosition);
        if (!safety.IsSafe)
        {
            Halt(safety.Reason);
            return;
        }

        if (retainerListings.IsTalkOpen && State is AutomationState.WaitingForRetainerMenu or AutomationState.WaitingForRetainerList)
        {
            retainerListings.AdvanceTalk();
            return;
        }

        switch (State)
        {
            case AutomationState.WaitingBeforeRetainerSelection:
                if (DelayElapsed()) SelectCurrentRetainer();
                break;
            case AutomationState.WaitingForRetainerMenu:
                if (retainerListings.IsRetainerMenuOpen)
                    Schedule(AutomationState.WaitingBeforeOpeningSellList, $"Opening {retainerListings.ActiveRetainerName}'s market listings.");
                else
                    CheckTimeout("Timed out waiting for the selected retainer.");
                break;
            case AutomationState.WaitingBeforeOpeningSellList:
                if (DelayElapsed()) OpenSellList();
                break;
            case AutomationState.WaitingForSellList:
                if (retainerListings.IsSellListOpen)
                    Schedule(AutomationState.WaitingForStableInterface, $"Waiting for {retainerListings.ActiveRetainerName}'s listings to stabilize.");
                else
                    CheckTimeout("Timed out waiting for the retainer sell list.");
                break;
            case AutomationState.WaitingForStableInterface:
                if (DelayElapsed()) Transition(AutomationState.ReadingListings, "Reading current retainer listings.");
                break;
            case AutomationState.ReadingListings:
                ReadListings();
                break;
            case AutomationState.WaitingBeforeOpeningListing:
                if (DelayElapsed()) OpenCurrentListing();
                break;
            case AutomationState.WaitingForContextMenu:
                if (retainerListings.IsContextMenuOpen)
                    Schedule(AutomationState.WaitingBeforeOpeningPriceEditor, $"Opening Adjust Price for {queue[currentIndex].Listing.ItemName}.");
                else
                    CheckTimeout("Timed out waiting for the listing context menu.");
                break;
            case AutomationState.WaitingBeforeOpeningPriceEditor:
                if (DelayElapsed()) OpenPriceEditor();
                break;
            case AutomationState.WaitingForPriceEditor:
                if (retainerListings.IsPriceEditorOpen)
                    RequestCurrentMarket();
                else
                    CheckTimeout("Timed out waiting for the Adjust Price window.");
                break;
            case AutomationState.RequestingMarketData:
                PollMarketRequest();
                break;
            case AutomationState.EvaluatingPrice:
                EvaluateCurrentListing();
                break;
            case AutomationState.WaitingBeforeCommit:
                if (DelayElapsed()) CommitOrDisarm();
                break;
            case AutomationState.WaitingAfterCommit:
                if (DelayElapsed()) VerifyCommitAndMoveNext();
                break;
            case AutomationState.WaitingBeforeClosingSellList:
                if (DelayElapsed()) CloseCurrentSellList();
                break;
            case AutomationState.WaitingForRetainerMenuAfterSellList:
                if (retainerListings.IsRetainerMenuOpen)
                    Schedule(AutomationState.WaitingBeforeClosingRetainer, $"Dismissing {retainerListings.ActiveRetainerName}.");
                else
                    CheckTimeout("Timed out returning to the retainer menu.");
                break;
            case AutomationState.WaitingBeforeClosingRetainer:
                if (DelayElapsed()) CloseCurrentRetainer();
                break;
            case AutomationState.WaitingForRetainerList:
                if (retainerListings.IsRetainerListOpen)
                    ContinueWithNextRetainer();
                else
                    CheckTimeout("Timed out returning to the summoning-bell retainer list.");
                break;
        }
    }

    private void TrackInterfaceLifecycle()
    {
        if (State is not (AutomationState.Idle or AutomationState.Completed or AutomationState.Halted or AutomationState.Faulted))
            return;
        if (!retainerListings.IsRetainerListOpen)
            handledBell = false;
        if (!retainerListings.IsSellListOpen)
            handledSellList = false;

        if (State is AutomationState.Halted or AutomationState.Faulted &&
            !retainerListings.IsRetainerListOpen && !retainerListings.IsRetainerMenuOpen &&
            !retainerListings.IsSellListOpen && !retainerListings.IsPriceEditorOpen)
        {
            State = AutomationState.Idle;
            detail = "Open a summoning bell to begin.";
        }
    }

    private void TryAutoStart()
    {
        if (State is AutomationState.Halted or AutomationState.Faulted)
            return;
        if (!configuration.Current.AutomationEnabled)
            return;

        if (retainerListings.IsRetainerListOpen && !handledBell)
        {
            BeginBellSession();
            return;
        }

        if (retainerListings.IsSellListOpen && !handledSellList && !retainerListings.IsRetainerListOpen)
            BeginCurrentRetainerSession();
    }

    private void BeginBellSession()
    {
        ResetSession();
        bellSession = true;
        retainerCount = retainerListings.RetainerCount;
        handledBell = true;
        RetainerInterfaceOpened?.Invoke();
        if (retainerCount <= 0)
        {
            Halt("No retainers were available in the summoning-bell list.");
            return;
        }

        Schedule(AutomationState.WaitingBeforeRetainerSelection, $"Starting all-retainer run ({retainerCount} retainers).");
        LogSessionStart();
    }

    private void BeginCurrentRetainerSession()
    {
        ResetSession();
        bellSession = false;
        retainerCount = 1;
        handledSellList = true;
        RetainerInterfaceOpened?.Invoke();
        Schedule(AutomationState.WaitingForStableInterface, $"Starting with {retainerListings.ActiveRetainerName}.");
        LogSessionStart();
    }

    private void ResetSession()
    {
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        sessionCancellation = new CancellationTokenSource();
        queue.Clear();
        currentIndex = 0;
        updatesSubmitted = 0;
        retainerIndex = 0;
        retainerCount = 0;
        currentMarket = null;
        currentDecision = null;
        marketTask = null;
        sessionPosition = retainerListings.GetPlayerPosition() ?? Vector3.Zero;
    }

    private void LogSessionStart() => log.Add(AutomationLogLevel.Information,
        configuration.Current.AllowAutomaticWrites
            ? "Automation started with server-confirmed price writes armed."
            : "Automation started in dry-run mode; no prices will be submitted.");

    private void SelectCurrentRetainer()
    {
        if (!retainerListings.SelectRetainer(retainerIndex))
        {
            Halt($"Could not select retainer {retainerIndex + 1}.");
            return;
        }
        WaitFor(AutomationState.WaitingForRetainerMenu, $"Waiting for retainer {retainerIndex + 1} of {retainerCount}.");
    }

    private void OpenSellList()
    {
        if (!retainerListings.SelectSellItems())
        {
            Halt("Could not select the retainer's market-listings menu entry.");
            return;
        }
        WaitFor(AutomationState.WaitingForSellList, "Waiting for the retainer sell list.");
    }

    private void ReadListings()
    {
        var listings = retainerListings.ReadCurrentListings();
        queue.Clear();
        queue.AddRange(listings.Select((listing, index) => new AutomationQueueEntry(index, listing, "Queued")));
        currentIndex = 0;
        handledSellList = true;
        log.Add(AutomationLogLevel.Information,
            $"{retainerListings.ActiveRetainerName}: queued {queue.Count} market listing(s).");
        if (queue.Count == 0)
        {
            FinishCurrentRetainer();
            return;
        }
        BeginCurrentListing();
    }

    private void BeginCurrentListing()
    {
        if (currentIndex >= queue.Count)
        {
            FinishCurrentRetainer();
            return;
        }
        ReplaceCurrent(queue[currentIndex] with { Status = "Opening listing" });
        Schedule(AutomationState.WaitingBeforeOpeningListing, $"Opening {queue[currentIndex].Listing.ItemName}.");
    }

    private void OpenCurrentListing()
    {
        if (!retainerListings.OpenListingContextMenu(currentIndex))
        {
            Halt($"Could not open listing {currentIndex + 1}.");
            return;
        }
        WaitFor(AutomationState.WaitingForContextMenu, "Waiting for the listing menu.");
    }

    private void OpenPriceEditor()
    {
        if (!retainerListings.SelectAdjustPrice())
        {
            Halt("Could not select Adjust Price.");
            return;
        }
        WaitFor(AutomationState.WaitingForPriceEditor, "Waiting for the Adjust Price window.");
    }

    private void RequestCurrentMarket()
    {
        var entry = queue[currentIndex];
        ReplaceCurrent(entry with { Status = "Reading live market" });
        currentMarket = null;
        currentDecision = null;
        marketTask = marketData.GetSnapshotAsync(entry.Listing.ItemId, sessionCancellation!.Token);
        if (!retainerListings.RequestComparePrices())
        {
            Halt("Could not click Compare Prices.");
            return;
        }
        Transition(AutomationState.RequestingMarketData, $"Reading live prices for {entry.Listing.ItemName}.");
    }

    private void PollMarketRequest()
    {
        if (marketTask is null || !marketTask.IsCompleted)
            return;

        retainerListings.CloseComparePrices();
        if (marketTask.IsCanceled || marketTask.IsFaulted)
        {
            var message = marketTask.Exception?.GetBaseException().Message ?? "Live market request timed out.";
            var entry = queue[currentIndex];
            ReplaceCurrent(entry with { Status = "Market data failed" });
            log.Add(AutomationLogLevel.Error, $"{entry.Listing.ItemName}: {message}");
            retainerListings.CancelPriceEditor();
            Schedule(AutomationState.WaitingAfterCommit, "Market data failed; moving to the next listing.");
            return;
        }

        currentMarket = marketTask.Result;
        Transition(AutomationState.EvaluatingPrice, $"Evaluating {queue[currentIndex].Listing.ItemName}.");
    }

    private void EvaluateCurrentListing()
    {
        var entry = queue[currentIndex];
        currentDecision = pricing.Evaluate(new PricingContext(
            entry.Listing,
            currentMarket!,
            configuration.Current.GetEffectiveRule(entry.Listing.ItemId),
            retainerListings.OwnedRetainerIds));
        ReplaceCurrent(entry with { Status = currentDecision.Kind.ToString(), Decision = currentDecision });

        if (!currentDecision.ShouldUpdate)
        {
            log.Add(AutomationLogLevel.Information,
                $"{entry.Listing.ItemName}: kept {entry.Listing.CurrentPrice:N0} gil; live lowest " +
                $"{FormatPrice(currentDecision.LowestMarketPrice)}. {currentDecision.Reason}");
            retainerListings.CancelPriceEditor();
            Schedule(AutomationState.WaitingAfterCommit, "No safe adjustment; moving to the next listing.");
            return;
        }

        log.Add(AutomationLogLevel.Information,
            $"{entry.Listing.ItemName}: live lowest {currentDecision.LowestMarketPrice:N0}; target {currentDecision.TargetPrice:N0} gil.");

        if (!configuration.Current.AllowAutomaticWrites)
        {
            ReplaceCurrent(queue[currentIndex] with { Status = "Dry-run update" });
            retainerListings.CancelPriceEditor();
            Schedule(AutomationState.WaitingAfterCommit, "Dry-run decision recorded.");
            return;
        }

        if (updatesSubmitted >= configuration.Current.MaximumUpdatesPerSession)
        {
            retainerListings.CancelPriceEditor();
            Complete($"Stopped at the configured session limit of {updatesSubmitted} update(s).");
            return;
        }

        Schedule(AutomationState.WaitingBeforeCommit,
            $"Waiting before updating {entry.Listing.ItemName} to {currentDecision.TargetPrice:N0} gil.");
    }

    private void CommitOrDisarm()
    {
        if (!configuration.Current.AllowAutomaticWrites)
        {
            retainerListings.CancelPriceEditor();
            ReplaceCurrent(queue[currentIndex] with { Status = "Disarmed before commit" });
            Schedule(AutomationState.WaitingAfterCommit, "Write disarmed; moving to the next listing.");
            return;
        }
        CommitCurrentPrice();
    }

    private void CommitCurrentPrice()
    {
        Transition(AutomationState.CommittingPrice, $"Submitting price for {queue[currentIndex].Listing.ItemName}.");
        var entry = queue[currentIndex];
        var target = currentDecision!.TargetPrice!.Value;
        var result = retainerListings.CommitPrice(entry.Listing, target);
        if (!result.Succeeded)
        {
            ReplaceCurrent(entry with { Status = "Commit rejected" });
            Halt(result.Message);
            return;
        }

        updatesSubmitted++;
        verificationDeadline = DateTimeOffset.UtcNow.AddSeconds(6);
        ReplaceCurrent(entry with { Status = $"Submitted {target:N0} gil" });
        log.Add(AutomationLogLevel.Information,
            $"SUBMITTED {entry.Listing.ItemName} ({entry.Listing.RetainerName}, slot {entry.Listing.Slot}): " +
            $"{entry.Listing.CurrentPrice:N0} -> {target:N0} gil. {result.Message}");
        Schedule(AutomationState.WaitingAfterCommit, "Waiting for the server-confirmed listing update.");
    }

    private void VerifyCommitAndMoveNext()
    {
        var entry = queue[currentIndex];
        if (currentDecision?.ShouldUpdate == true && entry.Status.StartsWith("Submitted", StringComparison.Ordinal))
        {
            var target = currentDecision.TargetPrice!.Value;
            if (!retainerListings.TryReadListing(entry.Listing.Slot, out var current) || current is null ||
                current.RetainerId != entry.Listing.RetainerId || current.ItemId != entry.Listing.ItemId ||
                current.CurrentPrice != target)
            {
                if (DateTimeOffset.UtcNow < verificationDeadline)
                {
                    nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(250);
                    return;
                }
                Halt($"The server did not confirm {target:N0} gil for {entry.Listing.ItemName}.");
                return;
            }
            ReplaceCurrent(entry with { Status = $"Verified {target:N0} gil" });
            log.Add(AutomationLogLevel.Information,
                $"VERIFIED {entry.Listing.ItemName} at {target:N0} gil on {entry.Listing.RetainerName}.");
        }

        currentIndex++;
        marketTask = null;
        currentMarket = null;
        currentDecision = null;
        BeginCurrentListing();
    }

    private void FinishCurrentRetainer()
    {
        log.Add(AutomationLogLevel.Information,
            $"Finished {retainerListings.ActiveRetainerName}: processed {queue.Count} listing(s).");
        if (!bellSession)
        {
            Complete($"Processed {queue.Count} listing(s); submitted {updatesSubmitted} update(s).");
            return;
        }
        Schedule(AutomationState.WaitingBeforeClosingSellList, "Returning to the retainer menu.");
    }

    private void CloseCurrentSellList()
    {
        if (!retainerListings.CloseSellList())
        {
            Halt("Could not close the retainer sell list.");
            return;
        }
        WaitFor(AutomationState.WaitingForRetainerMenuAfterSellList, "Waiting for the retainer menu.");
    }

    private void CloseCurrentRetainer()
    {
        if (!retainerListings.CloseRetainerMenu())
        {
            Halt("Could not dismiss the active retainer.");
            return;
        }
        WaitFor(AutomationState.WaitingForRetainerList, "Waiting for the summoning-bell retainer list.", 15);
    }

    private void ContinueWithNextRetainer()
    {
        retainerIndex++;
        queue.Clear();
        currentIndex = 0;
        if (retainerIndex >= retainerCount)
        {
            Complete($"Finished all {retainerCount} retainers; submitted {updatesSubmitted} update(s).");
            return;
        }
        Schedule(AutomationState.WaitingBeforeRetainerSelection,
            $"Continuing with retainer {retainerIndex + 1} of {retainerCount}.");
    }

    private void Complete(string message)
    {
        State = AutomationState.Completed;
        detail = message;
        log.Add(AutomationLogLevel.Information, message);
    }

    private void Schedule(AutomationState state, string message)
    {
        var config = configuration.Current;
        var delay = Random.Shared.Next(config.MinimumDelayMs, config.MaximumDelayMs + 1);
        nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(delay);
        Transition(state, message);
    }

    private void WaitFor(AutomationState state, string message, int seconds = 10)
    {
        stateDeadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        Transition(state, message);
    }

    private void CheckTimeout(string message)
    {
        if (DateTimeOffset.UtcNow >= stateDeadline)
            Halt(message);
    }

    private bool DelayElapsed() => DateTimeOffset.UtcNow >= nextActionAt;

    private void Transition(AutomationState state, string message)
    {
        State = state;
        detail = message;
    }

    private void ReplaceCurrent(AutomationQueueEntry entry) => queue[currentIndex] = entry;

    private static string FormatPrice(uint? price) => price.HasValue ? $"{price.Value:N0} gil" : "none";

    private static bool IsDelayState(AutomationState state) => state is
        AutomationState.WaitingBeforeRetainerSelection or
        AutomationState.WaitingBeforeOpeningSellList or
        AutomationState.WaitingForStableInterface or
        AutomationState.WaitingBeforeOpeningListing or
        AutomationState.WaitingBeforeOpeningPriceEditor or
        AutomationState.WaitingBeforeCommit or
        AutomationState.WaitingAfterCommit or
        AutomationState.WaitingBeforeClosingSellList or
        AutomationState.WaitingBeforeClosingRetainer;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
    }
}
