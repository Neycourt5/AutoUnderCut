using System.Numerics;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public enum AutomationState
{
    Idle,
    WaitingForAvailableRetainers,
    WaitingBeforeRetainerSelection,
    WaitingForRetainerMenu,
    WaitingBeforeOpeningSellList,
    WaitingForSellList,
    WaitingForStableInterface,
    WaitingAfterAutoListing,
    ReadingListings,
    WaitingBeforeOpeningListing,
    WaitingForContextMenu,
    WaitingBeforeOpeningPriceEditor,
    WaitingForPriceEditor,
    WaitingForPriceEditorStable,
    WaitingBeforeMarketRequest,
    RequestingMarketData,
    EvaluatingPrice,
    WaitingBeforeCommit,
    CommittingPrice,
    WaitingAfterCommit,
    WaitingBeforeClosingSellList,
    WaitingForRetainerMenuAfterSellList,
    WaitingBeforeGilCollection,
    WaitingForBank,
    WaitingBeforeGilAmount,
    WaitingBeforeGilConfirmation,
    WaitingForGilVerification,
    WaitingBeforeClosingRetainer,
    WaitingForRetainerList,
    WaitingForScheduledRun,
    RecoveringRetainerInterface,
    WaitingToRetryRetainers,
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

public sealed class AutomationController : IRetainerAutomation, IDisposable
{
    private static readonly TimeSpan SameItemMarketReuseWindow = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> CuratedAutoListItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Grade 4 Gemdraught of Strength",
        "Grade 4 Gemdraught of Dexterity",
        "Grade 4 Gemdraught of Intelligence",
        "Grade 4 Gemdraught of Mind",
        "Caramel Popcorn",
    };
    private readonly IFramework framework;
    private readonly IRetainerListingService retainerListings;
    private readonly IMarketDataService marketData;
    private readonly IPricingStrategyService pricing;
    private readonly IPortfolioValuationService portfolioValuation;
    private readonly ConfigurationService configuration;
    private readonly ProcurementLedger procurementLedger;
    private readonly AutomationLog log;
    private readonly TimeProvider timeProvider;
    private readonly RetainerRunSchedule runSchedule = new();
    private readonly List<AutomationQueueEntry> queue = [];
    private readonly HashSet<short> processedSlots = [];
    private readonly HashSet<short> freshlyRepricedAutoListingSlots = [];
    // A recovery restarts the whole pass from the bell, so a row that fails the same
    // way every time is reopened forever. Track the failures and give up on the row.
    private readonly Dictionary<(ulong Retainer, short Slot), int> listingFailureCounts = [];
    private readonly HashSet<(ulong Retainer, short Slot)> skippedProblemListings = [];
    private int consecutiveRecoveries;
    private string? lastWriteFailure;
    private readonly Dictionary<uint, (DateTimeOffset At, IReadOnlyList<MarketListing> Listings)> observedHomePrices = [];
    private readonly List<int> retainerRows = [];
    private readonly Dictionary<(ulong RetainerId, short Slot), PortfolioListingEstimate> portfolioListings = [];
    private readonly Dictionary<ulong, PortfolioRetainerBalance> portfolioRetainers = [];
    private readonly Dictionary<ulong, int> fillListingCounts = [];
    private readonly Dictionary<(ulong RetainerId, short Slot), (uint ItemId, uint Price)> knownSafeListingPrices = [];
    private readonly Dictionary<ulong, IReadOnlyList<RetainerListing>> scannedStock = [];

    private CancellationTokenSource? sessionCancellation;
    private Task<MarketSnapshot>? marketTask;
    private MarketSnapshot? currentMarket;
    private MarketSnapshot? reusableMarket;
    private PriceDecision? currentDecision;
    private PendingAutoListing? pendingAutoListing;
    private Vector3 sessionPosition;
    private DateTimeOffset nextActionAt;
    private DateTimeOffset stateDeadline;
    private DateTimeOffset verificationDeadline;
    private DateTimeOffset lastMarketRequestAt;
    private int currentIndex;
    private int marketRequestAttempts;
    private bool currentRowMapped;
    private int updatesSubmitted;
    private int retainerIndex;
    private int retainerCount;
    private int listingsSeenAcrossRetainers;
    private int portfolioExpectedRetainers;
    private DateTimeOffset? portfolioStartedAt;
    private DateTimeOffset? portfolioCompletedAt;
    private bool portfolioFullBellRun;
    private bool portfolioComplete;
    private bool bellSession;
    private bool handledBell;
    private bool handledSellList;
    private bool returnToAutoListingAfterCurrent;
    private bool requestedFillOnlyRun;
    private bool fillOnlyRun;
    private bool abortFillRunAtBell;
    private int autoListingProbeRowLimit;
    private uint retainerGilBeforeWithdrawal;
    private uint pendingGilWithdrawal;
    private string abortFillReason = string.Empty;
    private string detail = "Open a summoning bell to begin.";

    public AutomationController(
        IFramework framework,
        IRetainerListingService retainerListings,
        IMarketDataService marketData,
        IPricingStrategyService pricing,
        IPortfolioValuationService portfolioValuation,
        ConfigurationService configuration,
        ProcurementLedger procurementLedger,
        AutomationLog log,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.retainerListings = retainerListings;
        this.marketData = marketData;
        this.pricing = pricing;
        this.portfolioValuation = portfolioValuation;
        this.configuration = configuration;
        this.procurementLedger = procurementLedger;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        framework.Update += OnFrameworkUpdate;
    }

    public event Action? RetainerInterfaceOpened;
    public Func<bool>? IsStartBlocked { get; set; }
    public AutomationState State { get; private set; } = AutomationState.Idle;
    public int? LastKnownFreeSaleSlots { get; private set; }
    public bool RequiresManualRestart { get; private set; }
    public string? LastWriteFailure => lastWriteFailure;

    /// <summary>
    /// Home-world prices seen while repricing. Every retainer pass reads the live
    /// board for each listing anyway, so shopping can reuse those instead of paying
    /// for a separate sweep of the same items.
    /// </summary>
    public IReadOnlyDictionary<uint, (DateTimeOffset At, IReadOnlyList<MarketListing> Listings)> ObservedHomePrices =>
        observedHomePrices;
    public IReadOnlyList<StockExposure> ListedStock { get; private set; } = [];
    public bool IsActive => State is not (AutomationState.Idle or AutomationState.Completed or AutomationState.Halted or AutomationState.Faulted or AutomationState.WaitingForScheduledRun);

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

    public bool TryGetKnownSafePrice(uint itemId, out uint price)
    {
        var values = knownSafeListingPrices.Values
            .Where(x => x.ItemId == itemId)
            .Select(x => x.Price)
            .Order()
            .ToArray();
        if (values.Length == 0)
        {
            price = 0;
            return false;
        }

        var middle = values.Length / 2;
        price = values.Length % 2 == 1
            ? values[middle]
            : (uint)(((ulong)values[middle - 1] + values[middle]) / 2);
        return true;
    }

    public PortfolioValuation PortfolioSnapshot() => portfolioValuation.Calculate(
        portfolioListings.Values.ToArray(),
        portfolioRetainers.Values.ToArray(),
        retainerListings.PlayerGil,
        portfolioStartedAt,
        portfolioCompletedAt,
        portfolioFullBellRun,
        portfolioComplete,
        portfolioExpectedRetainers);

    public void StartNow()
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        RequiresManualRestart = false;
        requestedFillOnlyRun = false;
        ForgetProblemListings();
        if (retainerListings.IsRetainerListOpen)
            BeginBellSession();
        else if (retainerListings.IsSellListOpen)
            BeginCurrentRetainerSession();
        else
            Halt("Cannot start: open the summoning-bell retainer list or a retainer sell list first.");
    }

    public void StartBagListingNow()
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        RequiresManualRestart = false;
        requestedFillOnlyRun = true;
        ForgetProblemListings();
        if (retainerListings.IsRetainerListOpen)
            BeginBellSession();
        else
            Halt("Cannot start bag filling: open the main summoning-bell retainer list first.");
    }

    public void Halt(string reason = "Stopped by user.")
    {
        RequiresManualRestart = true;
        sessionCancellation?.Cancel();
        marketTask = null;
        currentMarket = null;
        currentDecision = null;
        retainerListings.CloseComparePrices();
        retainerListings.CloseContextMenu();
        if (retainerListings.IsPriceEditorOpen)
            retainerListings.CancelPriceEditor();
        retainerListings.CancelBankDialog();
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
            RequiresManualRestart = true;
            sessionCancellation?.Cancel();
            retainerListings.CancelBankDialog();
            State = AutomationState.Faulted;
            detail = ex.Message;
            log.Add(AutomationLogLevel.Error, $"Automation faulted: {ex}");
        }
    }

    private void Tick()
    {
        if (IsStartBlocked?.Invoke() == true)
            return;
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
            if (timeProvider.GetUtcNow() >= stateDeadline)
                RecoverInterface("Retainer dialogue did not finish before the timeout.");
            else if (DelayElapsed())
            {
                retainerListings.AdvanceTalk();
                nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(500);
            }
            return;
        }

        switch (State)
        {
            case AutomationState.WaitingForAvailableRetainers:
                if (retainerListings.IsRetainerListOpen && retainerListings.AvailableRetainerIndices.Count > 0)
                    BeginBellSession();
                else
                    CheckTimeout("Retainer data did not become ready. The available-retainer check will retry.");
                break;
            case AutomationState.RecoveringRetainerInterface:
                PollInterfaceRecovery();
                break;
            case AutomationState.WaitingToRetryRetainers:
                if (!configuration.Current.AutomationEnabled || !configuration.Current.RepeatBellRuns)
                    Complete("Automatic retainer retries were disabled.");
                else if (DelayElapsed() && retainerListings.IsRetainerListOpen)
                    BeginBellSession();
                else if (timeProvider.GetUtcNow() >= nextActionAt.AddMinutes(1))
                    Halt("The bell closed before the retry. Open the bell and press Start.");
                break;
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
            case AutomationState.WaitingAfterAutoListing:
                if (DelayElapsed()) VerifyAutoListing();
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
                    Schedule(AutomationState.WaitingForPriceEditorStable,
                        $"Waiting for the Adjust Price fields for {queue[currentIndex].Listing.ItemName}.");
                else
                    CheckTimeout("Timed out waiting for the Adjust Price window.");
                break;
            case AutomationState.WaitingForPriceEditorStable:
                if (DelayElapsed()) PrepareCurrentMarket();
                break;
            case AutomationState.WaitingBeforeMarketRequest:
                if (DelayElapsed()) RequestCurrentMarket();
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
                {
                    if (configuration.Current.AutomaticallyCollectRetainerGil && retainerListings.ActiveRetainerGil > 0)
                        Schedule(AutomationState.WaitingBeforeGilCollection,
                            $"Collecting gil from {retainerListings.ActiveRetainerName}.");
                    else
                        Schedule(AutomationState.WaitingBeforeClosingRetainer,
                            $"Dismissing {retainerListings.ActiveRetainerName}.");
                }
                else
                    CheckTimeout("Timed out returning to the retainer menu.");
                break;
            case AutomationState.WaitingBeforeGilCollection:
                if (DelayElapsed()) StartGilCollection();
                break;
            case AutomationState.WaitingForBank:
                if (retainerListings.IsBankOpen)
                    Schedule(AutomationState.WaitingBeforeGilAmount, "Preparing the retainer gil withdrawal amount.");
                else if (timeProvider.GetUtcNow() >= stateDeadline)
                    SkipGilCollection("Timed out opening the retainer gil window.");
                break;
            case AutomationState.WaitingBeforeGilAmount:
                if (DelayElapsed()) PrepareGilWithdrawal();
                break;
            case AutomationState.WaitingBeforeGilConfirmation:
                if (DelayElapsed()) ConfirmGilWithdrawal();
                break;
            case AutomationState.WaitingForGilVerification:
                PollGilWithdrawal();
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
            case AutomationState.WaitingForScheduledRun:
                if (!configuration.Current.RepeatBellRuns)
                    Complete("Repeated retainer runs were disabled.");
                else if (!retainerListings.IsRetainerListOpen)
                    Halt("The summoning-bell list closed; scheduled runs were cancelled.");
                else if (DelayElapsed())
                    BeginBellSession();
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

        if (!RequiresManualRestart && (State is AutomationState.Halted or AutomationState.Faulted) &&
            !retainerListings.IsRetainerListOpen && !retainerListings.IsRetainerMenuOpen &&
            !retainerListings.IsSellListOpen && !retainerListings.IsPriceEditorOpen &&
            !retainerListings.IsBankOpen)
        {
            State = AutomationState.Idle;
            detail = "Open a summoning bell to begin.";
        }
    }

    private void TryAutoStart()
    {
        if (RequiresManualRestart)
            return;
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
        retainerRows.Clear();
        retainerRows.AddRange(retainerListings.AvailableRetainerIndices);
        if (!configuration.Current.ProcessAllRetainers && retainerRows.Count > 1)
            retainerRows.RemoveRange(1, retainerRows.Count - 1);
        retainerCount = retainerRows.Count;
        handledBell = true;
        RetainerInterfaceOpened?.Invoke();
        if (retainerCount <= 0)
        {
            LastKnownFreeSaleSlots = null;
            WaitFor(AutomationState.WaitingForAvailableRetainers,
                "Waiting for the game to load the available retainer list.", 30);
            return;
        }

        ResetPortfolio(isFullBellRun: configuration.Current.ProcessAllRetainers);

        Schedule(AutomationState.WaitingBeforeRetainerSelection,
            configuration.Current.ProcessAllRetainers
                ? $"Starting automatic run across all {retainerCount} retainers."
                : "Starting a single-retainer run.");
        LogSessionStart();
    }

    private void BeginCurrentRetainerSession()
    {
        ResetSession();
        bellSession = false;
        retainerCount = 1;
        ResetPortfolio(isFullBellRun: false);
        handledSellList = true;
        RetainerInterfaceOpened?.Invoke();
        Schedule(AutomationState.WaitingForStableInterface, $"Starting with {retainerListings.ActiveRetainerName}.");
        LogSessionStart();
    }

    private void ResetSession()
    {
        scannedStock.Clear();
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        sessionCancellation = new CancellationTokenSource();
        queue.Clear();
        processedSlots.Clear();
        freshlyRepricedAutoListingSlots.Clear();
        retainerRows.Clear();
        currentIndex = 0;
        updatesSubmitted = 0;
        retainerIndex = 0;
        retainerCount = 0;
        currentMarket = null;
        reusableMarket = null;
        currentDecision = null;
        pendingAutoListing = null;
        returnToAutoListingAfterCurrent = false;
        fillOnlyRun = requestedFillOnlyRun;
        abortFillRunAtBell = false;
        autoListingProbeRowLimit = 0;
        retainerGilBeforeWithdrawal = 0;
        pendingGilWithdrawal = 0;
        abortFillReason = string.Empty;
        marketTask = null;
        lastMarketRequestAt = DateTimeOffset.MinValue;
        listingsSeenAcrossRetainers = 0;
        fillListingCounts.Clear();
        sessionPosition = retainerListings.GetPlayerPosition() ?? Vector3.Zero;
    }

    private void ResetPortfolio(bool isFullBellRun)
    {
        portfolioListings.Clear();
        portfolioRetainers.Clear();
        portfolioStartedAt = timeProvider.GetUtcNow();
        portfolioCompletedAt = null;
        portfolioFullBellRun = isFullBellRun;
        portfolioComplete = false;
        portfolioExpectedRetainers = retainerCount;
    }

    private void LogSessionStart() => log.Add(AutomationLogLevel.Information,
        configuration.Current.AllowAutomaticWrites
            ? "Automation started with server-confirmed price writes armed."
            : "Automation started in dry-run mode; no prices will be submitted.");

    private void SelectCurrentRetainer()
    {
        if (retainerIndex >= retainerRows.Count || !retainerListings.SelectRetainer(retainerRows[retainerIndex]))
        {
            RecoverInterface($"Could not select retainer {retainerIndex + 1}.");
            return;
        }
        WaitFor(AutomationState.WaitingForRetainerMenu, $"Waiting for retainer {retainerIndex + 1} of {retainerCount}.");
    }

    private void OpenSellList()
    {
        if (!retainerListings.SelectSellItems())
        {
            RecoverInterface("Could not select the retainer's market-listings menu entry.");
            return;
        }
        WaitFor(AutomationState.WaitingForSellList, "Waiting for the retainer sell list.");
    }

    private void ReadListings()
    {
        if (fillOnlyRun)
        {
            var currentListings = retainerListings.ReadCurrentListings();
            RememberSafePrices(currentListings);
            var countKey = retainerListings.ActiveRetainerId != 0
                ? retainerListings.ActiveRetainerId
                : (ulong)(retainerIndex + 1);
            fillListingCounts[countKey] = currentListings.Count;
            // Skipping slots already attempted this run is what stops the fill pass
            // reopening the same unresolved listing forever: its price is unchanged,
            // so without this it matches again on every pass.
            var unresolvedSeed = currentListings.FirstOrDefault(x =>
                IsUnresolvedCuratedPrice(x) &&
                !freshlyRepricedAutoListingSlots.Contains(x.Slot) &&
                !skippedProblemListings.Contains((x.RetainerId, x.Slot)) &&
                CuratedAutoListItems.Contains(x.ItemName));
            if (unresolvedSeed is not null)
            {
                log.Add(AutomationLogLevel.Warning,
                    $"{retainerListings.ActiveRetainerName}: repairing existing safety-seeded {unresolvedSeed.ItemName} before placing more stock.");
                BeginSafetySeedPricing(unresolvedSeed, currentListings.Count);
                return;
            }

            if (configuration.Current.AllowAutomaticListing &&
                retainerListings.TryAutoListPurchase(procurementLedger, out var fillListing) && fillListing is not null)
            {
                pendingAutoListing = fillListing;
                verificationDeadline = timeProvider.GetUtcNow().AddSeconds(10);
                Schedule(AutomationState.WaitingAfterAutoListing,
                    $"Listing {fillListing.ItemName} x{fillListing.Quantity} on {retainerListings.ActiveRetainerName}.");
                return;
            }

            log.Add(AutomationLogLevel.Information,
                currentListings.Count >= 20
                    ? $"{retainerListings.ActiveRetainerName}: already has 20/20 listings; skipped."
                    : $"{retainerListings.ActiveRetainerName}: no additional eligible 99-stack could be placed; skipped existing-price scan.");
            FinishCurrentRetainer();
            return;
        }

        if (configuration.Current.AllowAutomaticListing &&
            retainerListings.TryAutoListPurchase(procurementLedger, out var autoListing) && autoListing is not null)
        {
            pendingAutoListing = autoListing;
            verificationDeadline = timeProvider.GetUtcNow().AddSeconds(10);
            Schedule(AutomationState.WaitingAfterAutoListing,
                $"Listing purchased {autoListing.ItemName} x{autoListing.Quantity} on {retainerListings.ActiveRetainerName}.");
            return;
        }

        var listings = retainerListings.ReadCurrentListings();
        RememberSafePrices(listings);
        CapturePortfolioListings(listings);
        queue.Clear();
        processedSlots.Clear();
        queue.AddRange(listings.Select((listing, index) => (listing, index))
            .Where(x => !freshlyRepricedAutoListingSlots.Contains(x.listing.Slot))
            .Where(x => !skippedProblemListings.Contains((x.listing.RetainerId, x.listing.Slot)))
            .Select(x => new AutomationQueueEntry(x.index, x.listing, "Queued")));
        listingsSeenAcrossRetainers += listings.Count;
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

    private void CapturePortfolioListings(IReadOnlyList<RetainerListing> listings)
    {
        var retainerId = retainerListings.ActiveRetainerId;
        if (retainerId == 0 && listings.Count > 0)
            retainerId = listings[0].RetainerId;
        var retainerName = retainerListings.ActiveRetainerName;
        const decimal fallbackSellerFee = 5m;

        portfolioRetainers[retainerId] = new PortfolioRetainerBalance(
            retainerId, retainerName, retainerListings.ActiveRetainerGil, fallbackSellerFee);
        foreach (var listing in listings)
        {
            portfolioListings[(listing.RetainerId, listing.Slot)] = new PortfolioListingEstimate(
                listing.RetainerId,
                listing.RetainerName,
                listing.Slot,
                listing.ItemId,
                listing.ItemName,
                listing.Quantity,
                listing.CurrentPrice,
                listing.CurrentPrice,
                false,
                fallbackSellerFee);
        }
    }

    private void VerifyAutoListing()
    {
        if (pendingAutoListing is null)
        {
            Transition(AutomationState.ReadingListings, "Reading current retainer listings.");
            return;
        }
        if (!retainerListings.VerifyAutoListing(pendingAutoListing))
        {
            if (timeProvider.GetUtcNow() < verificationDeadline)
            {
                nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(250);
                return;
            }
            log.Add(AutomationLogLevel.Error,
                $"AUTO-LIST OUTCOME UNKNOWN {pendingAutoListing.ItemName} x{pendingAutoListing.Quantity}; stopping further listing attempts.");
            pendingAutoListing = null;
            if (fillOnlyRun)
            {
                AbortFreshAutoListing("The game did not verify the newly created retainer listing.");
                return;
            }
            Halt("Automatic listing outcome is unknown; stopped to prevent listing the same queued stock twice.");
            return;
        }

        var verified = pendingAutoListing;
        if (!MarketPriceSafety.IsSafeAutomaticUnitPrice(verified.ItemName, verified.UnitPrice, verified.Quantity))
        {
            pendingAutoListing = null;
            AbortFreshAutoListing(
                $"Rejected unsafe automatic listing price {verified.UnitPrice:N0} for {verified.ItemName}.");
            return;
        }
        procurementLedger.MarkListed(verified.ItemId, verified.IsHighQuality, verified.Quantity);
        RememberSafePrice(
            verified.ItemId,
            verified.UnitPrice,
            verified.Quantity,
            retainerListings.ActiveRetainerId,
            verified.MarketSlot);
        log.Add(AutomationLogLevel.Information,
            $"AUTO-LISTED {verified.ItemName} x{verified.Quantity} at validated {verified.UnitPrice:N0} gil on {retainerListings.ActiveRetainerName}.");
        pendingAutoListing = null;
        freshlyRepricedAutoListingSlots.Add(verified.MarketSlot);
        Transition(AutomationState.ReadingListings,
            "Validated listing created; looking for the next eligible 99-stack.");
    }

    private void BeginSafetySeedPricing(RetainerListing listing, int visibleRowCount)
    {
        queue.Clear();
        processedSlots.Clear();
        queue.Add(new AutomationQueueEntry(0, listing, "Probing visible rows for the exact safety seed"));
        currentIndex = 0;
        autoListingProbeRowLimit = visibleRowCount;
        returnToAutoListingAfterCurrent = true;
        BeginCurrentListing();
    }

    private void BeginCurrentListing()
    {
        if (currentIndex >= queue.Count)
        {
            FinishCurrentRetainer();
            return;
        }
        marketRequestAttempts = 0;
        currentRowMapped = false;
        ReplaceCurrent(queue[currentIndex] with { Status = "Opening listing" });
        Schedule(AutomationState.WaitingBeforeOpeningListing, $"Opening {queue[currentIndex].Listing.ItemName}.");
    }

    private void OpenCurrentListing()
    {
        if (!retainerListings.OpenListingContextMenu(queue[currentIndex].Index))
        {
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing(
                    $"Could not open visible row {queue[currentIndex].Index + 1} while locating the safety seed.");
                return;
            }
            RecoverInterface($"Could not open listing {queue[currentIndex].Index + 1}.");
            return;
        }
        WaitFor(AutomationState.WaitingForContextMenu, "Waiting for the listing menu.");
    }

    private void OpenPriceEditor()
    {
        if (!retainerListings.SelectAdjustPrice())
        {
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing("Could not open Adjust Price while locating the new safety seed.");
                return;
            }
            RecoverInterface("Could not select Adjust Price.");
            return;
        }
        WaitFor(AutomationState.WaitingForPriceEditor, "Waiting for the Adjust Price window.");
    }

    private void PrepareCurrentMarket()
    {
        CaptureSellerFee();
        currentMarket = null;
        currentDecision = null;
        marketTask = null;

        if (returnToAutoListingAfterCurrent)
        {
            var seedEntry = queue[currentIndex];
            if (!retainerListings.IsOpenPriceEditorFor(seedEntry.Listing, requirePriceMatch: true))
            {
                retainerListings.CancelPriceEditor();
                var nextRow = seedEntry.Index + 1;
                if (nextRow >= autoListingProbeRowLimit)
                {
                    AbortFreshAutoListing(
                        $"Could not find the exact safety-seeded {seedEntry.Listing.ItemName} row after checking {autoListingProbeRowLimit} visible listing(s).");
                    return;
                }

                queue[currentIndex] = seedEntry with
                {
                    Index = nextRow,
                    Status = $"Row {seedEntry.Index + 1} was a different item; probing row {nextRow + 1}",
                };
                Schedule(AutomationState.WaitingBeforeOpeningListing,
                    $"The opened row was not the new {seedEntry.Listing.ItemName}; trying visible row {nextRow + 1}.");
                return;
            }

            currentRowMapped = true;
            processedSlots.Add(seedEntry.Listing.Slot);
            log.Add(AutomationLogLevel.Debug,
                $"Validated visible row {seedEntry.Index + 1} as exact safety-seeded {seedEntry.Listing.ItemName}, market slot {seedEntry.Listing.Slot}.");
        }

        // Mapping immediately when the addon first appears was too early, but after
        // the stabilization delay it normally succeeds. Doing it before market I/O
        // keeps duplicate stacks aligned even if the price request later times out.
        TryMapCurrentPriceEditor(0);
        var entry = queue[currentIndex];

        if (TryReuseCurrentMarket(entry))
            return;

        var earliestRequestAt = lastMarketRequestAt + CurrentMarketCooldown();
        if (timeProvider.GetUtcNow() < earliestRequestAt)
        {
            ReplaceCurrent(queue[currentIndex] with { Status = "Waiting for market cooldown" });
            nextActionAt = earliestRequestAt;
            Transition(AutomationState.WaitingBeforeMarketRequest,
                $"Waiting before requesting live prices for {entry.Listing.ItemName}.");
            return;
        }

        RequestCurrentMarket();
    }

    private TimeSpan CurrentMarketCooldown() =>
        TimeSpan.FromMilliseconds(configuration.Current.MarketRequestCooldownMs);

    private void CaptureSellerFee()
    {
        if (!retainerListings.TryReadSellerFeePercent(out var feePercent))
            return;

        var retainerId = retainerListings.ActiveRetainerId;
        if (portfolioRetainers.TryGetValue(retainerId, out var balance))
            portfolioRetainers[retainerId] = balance with { SellerFeePercent = feePercent };

        foreach (var key in portfolioListings.Keys.Where(x => x.RetainerId == retainerId).ToArray())
            portfolioListings[key] = portfolioListings[key] with { SellerFeePercent = feePercent };
    }

    private void RequestCurrentMarket()
    {
        var entry = queue[currentIndex];
        if (!retainerListings.IsPriceEditorOpen)
        {
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing("The Adjust Price window closed before the guarded live request could run.");
                return;
            }
            RecoverInterface("The Adjust Price window closed before the market cooldown elapsed.");
            return;
        }
        ReplaceCurrent(entry with { Status = "Reading live market" });
        marketRequestAttempts++;
        marketTask = marketData.GetSnapshotAsync(entry.Listing.ItemId, sessionCancellation!.Token);
        if (!retainerListings.RequestComparePrices())
        {
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing("Could not request live prices for the safety-seeded listing.");
                return;
            }
            RecoverInterface("Could not click Compare Prices.");
            return;
        }
        lastMarketRequestAt = timeProvider.GetUtcNow();
        Transition(AutomationState.RequestingMarketData, $"Reading live prices for visible row {currentIndex + 1}.");
    }

    private void PollMarketRequest()
    {
        if (marketTask is null || !marketTask.IsCompleted)
            return;

        retainerListings.CloseComparePrices();
        if (marketTask.IsCanceled || marketTask.IsFaulted)
        {
            var message = marketTask.Exception?.GetBaseException().Message ?? "Live market request timed out.";
            var failedEntry = queue[currentIndex];
            if (!marketData.LastRequestSawAnyPacket)
                // The board answers an over-eager request with nothing at all. That is
                // not proof the item has no market: the very same items return full
                // listings a row or two later. Retry instead of writing the item off.
                log.Add(AutomationLogLevel.Debug,
                    $"The board returned nothing for {failedEntry.Listing.ItemName}; " +
                    "treating that as asked-too-soon and retrying.");
            if (marketRequestAttempts <= configuration.Current.MarketRequestRetryCount &&
                retainerListings.IsPriceEditorOpen)
            {
                TryMapCurrentPriceEditor(0);
                var totalAttempts = configuration.Current.MarketRequestRetryCount + 1;
                var retryDelay = configuration.Current.MarketRetryBackoffMs * marketRequestAttempts;
                var cooldownAt = lastMarketRequestAt + CurrentMarketCooldown();
                nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(retryDelay);
                if (cooldownAt > nextActionAt)
                    nextActionAt = cooldownAt;
                marketData.ClearCache();
                marketTask = null;
                currentMarket = null;
                ReplaceCurrent(failedEntry with
                {
                    Status = $"Retrying market data ({marketRequestAttempts + 1}/{totalAttempts})",
                });
                log.Add(AutomationLogLevel.Warning,
                    $"{failedEntry.Listing.ItemName}: market prices did not load on attempt " +
                    $"{marketRequestAttempts}/{totalAttempts}; retrying this row in " +
                    $"{Math.Ceiling((nextActionAt - timeProvider.GetUtcNow()).TotalSeconds):N0}s. {message} " +
                    $"[{marketData.LastRequestSummary}]");
                Transition(AutomationState.WaitingBeforeMarketRequest,
                    $"Waiting to retry live prices for {failedEntry.Listing.ItemName}.");
                return;
            }

            TryMapCurrentPriceEditor(0);
            failedEntry = queue[currentIndex];
            ReplaceCurrent(failedEntry with { Status = "Market data failed" });
            log.Add(AutomationLogLevel.Error,
                $"{failedEntry.Listing.ItemName}: market prices failed after {marketRequestAttempts} attempt(s); " +
                $"skipped. {message} [{marketData.LastRequestSummary}]");
            retainerListings.CancelPriceEditor();
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing(
                    $"Live prices failed for safety-seeded {failedEntry.Listing.ItemName}; it was left at the protected placeholder.");
                return;
            }
            Schedule(AutomationState.WaitingAfterCommit,
                "Market data failed after retries; moving to the next listing.");
            return;
        }

        currentMarket = marketTask.Result;
        if (currentMarket.ItemId != 0 && currentMarket.Listings.Count > 0)
            observedHomePrices[currentMarket.ItemId] = (timeProvider.GetUtcNow(), currentMarket.Listings);
        var entry = queue[currentIndex];
        if (currentMarket.ItemId != 0 && currentMarket.ItemId != entry.Listing.ItemId)
        {
            ReplaceCurrent(entry with { Status = "Ignored stale market packet" });
            log.Add(AutomationLogLevel.Error,
                $"{entry.Listing.ItemName}: ignored stale market packet for item #{currentMarket.ItemId}; expected #{entry.Listing.ItemId}.");
            retainerListings.CancelPriceEditor();
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing("A stale or mismatched market packet was received for the safety seed.");
                return;
            }
            Schedule(AutomationState.WaitingAfterCommit, "Stale market data was rejected; continuing safely.");
            return;
        }

        // RetainerSell's item fields are not consistently populated when the editor
        // first becomes visible. They are stable after Compare Prices has returned,
        // so map the visible row to its backing market slot here instead.
        if (!TryMapCurrentPriceEditor(currentMarket.ItemId))
        {
            ReplaceCurrent(entry with { Status = "Could not map visible row" });
            log.Add(AutomationLogLevel.Error,
                $"Visible row {currentIndex + 1}: live item #{currentMarket.ItemId} could not be mapped to an " +
                $"unused retainer market slot; skipped. Queue row says {entry.Listing.ItemName} " +
                $"(#{entry.Listing.ItemId}) x{entry.Listing.Quantity} at {entry.Listing.CurrentPrice:N0} in slot " +
                $"{entry.Listing.Slot}; {processedSlots.Count} slot(s) already claimed for this retainer.");
            retainerListings.CancelPriceEditor();
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing("The open Adjust Price item could not be proven to be the safety-seeded listing.");
                return;
            }
            Schedule(AutomationState.WaitingAfterCommit, "Could not map this row; continuing to the next listing.");
            return;
        }

        var resolved = queue[currentIndex].Listing;
        queue[currentIndex] = queue[currentIndex] with { Status = "Matched visible row" };
        reusableMarket = currentMarket;
        log.Add(AutomationLogLevel.Debug,
            $"Matched visible row {currentIndex + 1}, live item #{currentMarket.ItemId}, to {resolved.ItemName}, " +
            $"market slot {resolved.Slot}; aggregated {currentMarket.Listings.Count} live listing(s).");
        Transition(AutomationState.EvaluatingPrice, $"Evaluating {queue[currentIndex].Listing.ItemName}.");
    }

    private bool TryReuseCurrentMarket(AutomationQueueEntry entry)
    {
        var candidate = reusableMarket;
        if (candidate is null || candidate.ItemId != entry.Listing.ItemId ||
            timeProvider.GetUtcNow() - candidate.CapturedAt > SameItemMarketReuseWindow)
            return false;

        // If the stable editor still could not be mapped, fall back to a normal Compare
        // Prices request; never skip a row merely because the fast path was unavailable.
        if (!currentRowMapped)
            return false;

        var resolved = queue[currentIndex].Listing;
        var cachedMarket = candidate with { IsFromCache = true };
        currentMarket = cachedMarket;
        queue[currentIndex] = entry with { Listing = resolved, Status = "Reused same-item market data" };
        log.Add(AutomationLogLevel.Debug,
            $"Matched visible row {currentIndex + 1} to {resolved.ItemName}, market slot {resolved.Slot}; " +
            $"reused {cachedMarket.Listings.Count} listing(s) from the recent same-item search.");
        Transition(AutomationState.EvaluatingPrice,
            $"Reusing recent live prices for {resolved.ItemName}.");
        return true;
    }

    private bool TryMapCurrentPriceEditor(uint itemId)
    {
        if (currentRowMapped)
        {
            if (itemId == 0 || queue[currentIndex].Listing.ItemId == itemId)
                return true;
            // The first mapping runs before the live item id is known and matches on
            // name, quantity and price, so it can claim the wrong backing slot when a
            // retainer holds similar listings. Give that slot back and map again now
            // that the id is known, rather than refusing a perfectly valid row.
            log.Add(AutomationLogLevel.Debug,
                $"Visible row {currentIndex + 1} was provisionally mapped to " +
                $"{queue[currentIndex].Listing.ItemName} (slot {queue[currentIndex].Listing.Slot}); " +
                $"live item #{itemId} says otherwise, so remapping.");
            processedSlots.Remove(queue[currentIndex].Listing.Slot);
            currentRowMapped = false;
        }
        if (!retainerListings.TryResolveOpenPriceEditor(itemId, processedSlots, out var resolved) || resolved is null)
            return false;

        queue[currentIndex] = queue[currentIndex] with { Listing = resolved, Status = "Mapped visible row" };
        processedSlots.Add(resolved.Slot);
        currentRowMapped = true;
        return true;
    }

    private void EvaluateCurrentListing()
    {
        var entry = queue[currentIndex];
        var rule = configuration.Current.GetEffectiveRule(entry.Listing.ItemId);
        // A freshly created placeholder must never remain at the game's maximum price
        // just because the price-war guard fired. Use the guard's protected historical
        // floor for that first live pricing pass; the configured cost floor still wins.
        if (returnToAutoListingAfterCurrent && rule.PriceWarAction == PriceWarAction.LeaveUnchanged)
            rule.PriceWarAction = PriceWarAction.MatchProtectedFloor;
        currentDecision = pricing.Evaluate(new PricingContext(
            entry.Listing,
            currentMarket!,
            rule,
            retainerListings.OwnedRetainerIds));

        if (IsUnresolvedCuratedPrice(entry.Listing) &&
            (!currentDecision.ShouldUpdate || currentDecision.TargetPrice is not { } proposed ||
             !MarketPriceSafety.IsSafeCuratedUnitPrice(proposed, entry.Listing.Quantity)))
        {
            var repairTarget = ResolveSafetyRepairTarget(entry.Listing, currentDecision, currentMarket!);
            if (repairTarget.HasValue)
            {
                currentDecision = new PriceDecision(
                    PriceDecisionKind.Update,
                    entry.Listing.CurrentPrice,
                    repairTarget.Value,
                    currentDecision.LowestMarketPrice,
                    currentDecision.EffectiveFloor,
                    "Repaired an old automatic-listing placeholder using a validated same-item or historical price.");
            }
            else
            {
                ReplaceCurrent(entry with { Status = "Unsafe placeholder needs market data", Decision = currentDecision });
                log.Add(AutomationLogLevel.Error,
                    $"UNRESOLVED UNSAFE PRICE {entry.Listing.ItemName} at {entry.Listing.CurrentPrice:N0} gil: no validated repair price was available. The listing was not treated as a legitimate asking price.");
                retainerListings.CancelPriceEditor();
                Schedule(AutomationState.WaitingAfterCommit,
                    "Unsafe placeholder could not be repaired without trustworthy price data; continuing.");
                return;
            }
        }

        if (returnToAutoListingAfterCurrent && currentDecision.Kind == PriceDecisionKind.BelowFloor &&
            entry.Listing.CurrentPrice != currentDecision.EffectiveFloor)
        {
            currentDecision = new PriceDecision(
                PriceDecisionKind.Update,
                entry.Listing.CurrentPrice,
                currentDecision.EffectiveFloor,
                currentDecision.LowestMarketPrice,
                currentDecision.EffectiveFloor,
                "The live competitive target was below the saved purchase-cost floor; using the floor instead.");
        }
        UpdatePortfolioMarketEstimate(entry.Listing, currentDecision, currentMarket!);
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

    private void UpdatePortfolioMarketEstimate(
        RetainerListing listing,
        PriceDecision decision,
        MarketSnapshot market)
    {
        var key = (listing.RetainerId, listing.Slot);
        if (!portfolioListings.TryGetValue(key, out var estimate))
            return;

        uint estimatedPrice;
        if (decision.Kind == PriceDecisionKind.PriceWar && market.HistoricalMedianPrice is { } median)
        {
            var rule = configuration.Current.GetEffectiveRule(listing.ItemId);
            var protectedPrice = (uint)Math.Max(1m,
                decimal.Floor(median * (1m - (rule.PriceWarDropPercent / 100m))));
            estimatedPrice = Math.Min(listing.CurrentPrice, Math.Max(decision.EffectiveFloor, protectedPrice));
        }
        else if (decision.TargetPrice is { } target)
            estimatedPrice = target;
        else if (decision.LowestMarketPrice is { } lowest)
            estimatedPrice = Math.Min(listing.CurrentPrice, lowest);
        else
            estimatedPrice = listing.CurrentPrice;

        portfolioListings[key] = estimate with
        {
            EstimatedUnitPrice = estimatedPrice,
            HasLiveMarketEstimate = decision.LowestMarketPrice.HasValue,
        };
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
            log.Add(AutomationLogLevel.Error,
                $"{entry.Listing.ItemName}: update was skipped because the Adjust Price commit failed. {result.Message}");
            NoteListingWriteFailure(entry.Listing, result.Message);
            if (retainerListings.IsPriceEditorOpen)
                retainerListings.CancelPriceEditor();
            if (returnToAutoListingAfterCurrent)
            {
                AbortFreshAutoListing(
                    $"The live price write for safety-seeded {entry.Listing.ItemName} was rejected: {result.Message}");
                return;
            }
            Schedule(AutomationState.WaitingAfterCommit, "Commit failed; continuing to the next listing.");
            return;
        }

        updatesSubmitted++;
        verificationDeadline = timeProvider.GetUtcNow().AddSeconds(10);
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
                if (timeProvider.GetUtcNow() < verificationDeadline)
                {
                    nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(250);
                    return;
                }
                ReplaceCurrent(entry with { Status = "Server verification failed" });
                log.Add(AutomationLogLevel.Error,
                    $"{entry.Listing.ItemName}: server verification did not reach {target:N0} gil; skipped and continuing.");
                if (retainerListings.IsPriceEditorOpen)
                    retainerListings.CancelPriceEditor();
                if (returnToAutoListingAfterCurrent)
                {
                    AbortFreshAutoListing(
                        $"Server verification failed for safety-seeded {entry.Listing.ItemName}; no more stock will be listed.");
                    return;
                }
                MoveToNextListing();
                return;
            }
            ReplaceCurrent(entry with { Status = $"Verified {target:N0} gil" });
            RememberSafePrice(
                entry.Listing.ItemId,
                target,
                entry.Listing.Quantity,
                entry.Listing.RetainerId,
                entry.Listing.Slot);
            var key = (entry.Listing.RetainerId, entry.Listing.Slot);
            if (portfolioListings.TryGetValue(key, out var estimate))
            {
                portfolioListings[key] = estimate with
                {
                    AskingUnitPrice = target,
                    EstimatedUnitPrice = target,
                    HasLiveMarketEstimate = true,
                };
            }
            log.Add(AutomationLogLevel.Information,
                $"VERIFIED {entry.Listing.ItemName} at {target:N0} gil on {entry.Listing.RetainerName}.");
        }

        MoveToNextListing();
    }

    private void MoveToNextListing()
    {
        if (returnToAutoListingAfterCurrent)
        {
            var entry = queue[currentIndex];
            var settled = entry.Status.StartsWith("Verified", StringComparison.Ordinal) ||
                (currentDecision?.Kind is PriceDecisionKind.NoChange or PriceDecisionKind.WithinTolerance &&
                 entry.Listing.CurrentPrice < PricingStrategyService.MaximumListingPrice);
            // Record the attempt whichever way it went. Every other outcome - no safe
            // adjustment, a disarmed or failed write, stale market data - used to
            // leave the slot unmarked, so the pass reopened that one listing and
            // re-read its price indefinitely without ever changing anything.
            freshlyRepricedAutoListingSlots.Add(entry.Listing.Slot);
            if (!settled)
                log.Add(AutomationLogLevel.Warning,
                    $"{entry.Listing.ItemName}: could not settle its safety-seeded price this pass " +
                    $"({entry.Status}). Leaving it for the next run instead of reopening it.");
            returnToAutoListingAfterCurrent = false;
            marketTask = null;
            currentMarket = null;
            currentDecision = null;
            Transition(AutomationState.ReadingListings, "Live price committed; looking for the next eligible 99-stack.");
            return;
        }
        currentIndex++;
        marketTask = null;
        currentMarket = null;
        currentDecision = null;
        BeginCurrentListing();
    }

    private void AbortFreshAutoListing(string reason)
    {
        RequiresManualRestart = true;
        retainerListings.CloseComparePrices();
        if (retainerListings.IsPriceEditorOpen)
            retainerListings.CancelPriceEditor();
        procurementLedger.ClearBagStockQueue();
        pendingAutoListing = null;
        returnToAutoListingAfterCurrent = false;
        requestedFillOnlyRun = false;
        abortFillRunAtBell = true;
        abortFillReason = reason;
        log.Add(AutomationLogLevel.Error,
            $"AUTO-LIST ABORTED: {reason} Returning to the main summoning-bell list. No additional bag stock will be submitted this run.");
        Schedule(AutomationState.WaitingBeforeClosingSellList,
            "Stopping bag filling and returning safely to the main summoning-bell list.");
    }

    private void FinishCurrentRetainer()
    {
        // Real progress: the recovery budget only guards against never advancing.
        consecutiveRecoveries = 0;
        scannedStock[retainerListings.ActiveRetainerId] = retainerListings.ReadCurrentListings();
        log.Add(AutomationLogLevel.Information,
            $"Finished {retainerListings.ActiveRetainerName}: processed {queue.Count} listing(s).");
        if (!bellSession)
        {
            portfolioComplete = true;
            portfolioCompletedAt = timeProvider.GetUtcNow();
            Complete($"Processed {queue.Count} listing(s); submitted {updatesSubmitted} update(s).");
            return;
        }
        Schedule(AutomationState.WaitingBeforeClosingSellList, "Returning to the retainer menu.");
    }

    private void CloseCurrentSellList()
    {
        if (!retainerListings.CloseSellList())
        {
            RecoverInterface("Could not close the retainer sell list.");
            return;
        }
        WaitFor(AutomationState.WaitingForRetainerMenuAfterSellList, "Waiting for the retainer menu.");
    }

    private void CloseCurrentRetainer()
    {
        if (!retainerListings.CloseRetainerMenu())
        {
            RecoverInterface("Could not dismiss the active retainer.");
            return;
        }
        WaitFor(AutomationState.WaitingForRetainerList, "Waiting for the summoning-bell retainer list.", 15);
    }

    private void StartGilCollection()
    {
        if (!configuration.Current.AutomaticallyCollectRetainerGil)
        {
            SkipGilCollection("Automatic gil collection was disabled.");
            return;
        }
        retainerGilBeforeWithdrawal = retainerListings.ActiveRetainerGil;
        pendingGilWithdrawal = 0;
        if (retainerGilBeforeWithdrawal == 0)
        {
            Schedule(AutomationState.WaitingBeforeClosingRetainer,
                $"No gil to collect from {retainerListings.ActiveRetainerName}; dismissing retainer.");
            return;
        }
        if (!retainerListings.SelectEntrustGil(out var selectionMessage))
        {
            SkipGilCollection(selectionMessage);
            return;
        }
        WaitFor(AutomationState.WaitingForBank, $"{selectionMessage} Waiting for the retainer gil window.");
    }

    private void PrepareGilWithdrawal()
    {
        retainerGilBeforeWithdrawal = retainerListings.ActiveRetainerGil;
        if (!retainerListings.SetWithdrawAllRetainerGil(out pendingGilWithdrawal, out var message))
        {
            retainerListings.CancelBankDialog();
            SkipGilCollection(message);
            return;
        }
        Schedule(AutomationState.WaitingBeforeGilConfirmation, message);
    }

    private void ConfirmGilWithdrawal()
    {
        if (!configuration.Current.AutomaticallyCollectRetainerGil)
        {
            SkipGilCollection("Automatic gil collection was disabled before confirmation.");
            return;
        }
        if (!retainerListings.ConfirmGilWithdrawal())
        {
            SkipGilCollection("The retainer gil withdrawal confirmation was unavailable.");
            return;
        }
        WaitFor(AutomationState.WaitingForGilVerification,
            $"Waiting for the server to confirm {pendingGilWithdrawal:N0} gil collected.");
    }

    private void PollGilWithdrawal()
    {
        var expectedRemaining = retainerGilBeforeWithdrawal > pendingGilWithdrawal
            ? retainerGilBeforeWithdrawal - pendingGilWithdrawal
            : 0;
        var current = retainerListings.ActiveRetainerGil;
        if (current <= expectedRemaining)
        {
            var collected = retainerGilBeforeWithdrawal - current;
            var retainerId = retainerListings.ActiveRetainerId;
            if (portfolioRetainers.TryGetValue(retainerId, out var balance))
                portfolioRetainers[retainerId] = balance with { Gil = current };
            log.Add(AutomationLogLevel.Information,
                $"COLLECTED {collected:N0} gil from {retainerListings.ActiveRetainerName}; {current:N0} gil remains on the retainer.");
            Schedule(AutomationState.WaitingBeforeClosingRetainer,
                $"Gil collected; dismissing {retainerListings.ActiveRetainerName}.");
            return;
        }
        if (timeProvider.GetUtcNow() >= stateDeadline)
            SkipGilCollection($"Server verification did not confirm the {pendingGilWithdrawal:N0} gil withdrawal.");
    }

    private void SkipGilCollection(string reason)
    {
        retainerListings.CancelBankDialog();
        log.Add(AutomationLogLevel.Warning,
            $"GIL COLLECTION SKIPPED for {retainerListings.ActiveRetainerName}: {reason}");
        pendingGilWithdrawal = 0;
        Schedule(AutomationState.WaitingBeforeClosingRetainer,
            $"Gil collection skipped; dismissing {retainerListings.ActiveRetainerName}.");
    }

    private void ContinueWithNextRetainer()
    {
        if (abortFillRunAtBell)
        {
            var message = $"Bag filling stopped safely: {abortFillReason} Returned to the main summoning-bell list.";
            abortFillRunAtBell = false;
            fillOnlyRun = false;
            requestedFillOnlyRun = false;
            Complete(message);
            return;
        }

        retainerIndex++;
        queue.Clear();
        freshlyRepricedAutoListingSlots.Clear();
        returnToAutoListingAfterCurrent = false;
        currentIndex = 0;
        if (retainerIndex >= retainerCount)
        {
            portfolioComplete = true;
            portfolioCompletedAt = timeProvider.GetUtcNow();
            var finalListingCount = fillOnlyRun ? fillListingCounts.Values.Sum() : listingsSeenAcrossRetainers;
            LastKnownFreeSaleSlots = Math.Max(0, retainerCount * 20 - finalListingCount);
            if (configuration.Current.ProcessAllRetainers)
                ListedStock = scannedStock.Values.SelectMany(x => x)
                    .GroupBy(x => (x.ItemId, x.IsHighQuality))
                    .Select(x => new StockExposure(x.Key.ItemId, x.Key.IsHighQuality,
                        (uint)Math.Min(uint.MaxValue, x.Sum(y => (long)y.Quantity)), x.Count())).ToArray();
            procurementLedger.ClearCompleted();
            var completedFillOnly = fillOnlyRun;
            var message = $"Finished {retainerCount} retainer(s); submitted {updatesSubmitted} update(s).";
            if (fillOnlyRun)
            {
                fillOnlyRun = false;
                requestedFillOnlyRun = false;
            }
            if (configuration.Current.RepeatBellRuns)
                ScheduleNextBellRun(message, completedFillOnly);
            else
                Complete(message);
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

    private void ScheduleNextBellRun(string completedMessage, bool completedFillOnly)
    {
        var minimumMinutes = configuration.Current.RepeatMinimumMinutes;
        var maximumMinutes = configuration.Current.RepeatMaximumMinutes;
        var minutes = Random.Shared.Next(minimumMinutes, maximumMinutes + 1);
        nextActionAt = runSchedule.CompletePass(timeProvider.GetUtcNow(), TimeSpan.FromMinutes(minutes), completedFillOnly);
        Transition(AutomationState.WaitingForScheduledRun,
            $"{completedMessage} Next full retainer check at {nextActionAt.LocalDateTime:t}.");
        log.Add(AutomationLogLevel.Information,
            $"{completedMessage} Scheduled the next bell run for {nextActionAt.LocalDateTime:t}.");
    }

    private void Schedule(AutomationState state, string message)
    {
        var config = configuration.Current;
        var delay = Random.Shared.Next(config.MinimumDelayMs, config.MaximumDelayMs + 1);
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(delay);
        Transition(state, message);
    }

    private void RememberSafePrices(IEnumerable<RetainerListing> listings)
    {
        foreach (var listing in listings.Where(x => CuratedAutoListItems.Contains(x.ItemName)))
            RememberSafePrice(
                listing.ItemId,
                listing.CurrentPrice,
                listing.Quantity,
                listing.RetainerId,
                listing.Slot);
    }

    private void RememberSafePrice(
        uint itemId,
        uint price,
        uint quantity,
        ulong retainerId,
        short slot)
    {
        if (!MarketPriceSafety.IsSafeCuratedUnitPrice(price, quantity))
            return;
        knownSafeListingPrices[(retainerId, slot)] = (itemId, price);
    }

    private static bool IsUnresolvedCuratedPrice(RetainerListing listing) =>
        MarketPriceSafety.IsSafetySeedRepresentation(listing.CurrentPrice, listing.Quantity) ||
        listing.CurrentPrice > MarketPriceSafety.MaximumCuratedUnitPrice;

    private uint? ResolveSafetyRepairTarget(
        RetainerListing listing,
        PriceDecision decision,
        MarketSnapshot market)
    {
        uint? candidate = null;
        if (TryGetKnownSafePrice(listing.ItemId, out var known))
            candidate = known;
        else if (market.HistoricalMedianPrice is { } median &&
                 MarketPriceSafety.IsSafeCuratedUnitPrice(median, listing.Quantity))
            candidate = median;
        else if (decision.EffectiveFloor > 1 &&
                 MarketPriceSafety.IsSafeCuratedUnitPrice(decision.EffectiveFloor, listing.Quantity))
            candidate = decision.EffectiveFloor;

        if (!candidate.HasValue)
            return null;
        var target = Math.Max(candidate.Value, decision.EffectiveFloor);
        return MarketPriceSafety.IsSafeCuratedUnitPrice(target, listing.Quantity) ? target : null;
    }

    private void WaitFor(AutomationState state, string message, int seconds = 10)
    {
        stateDeadline = timeProvider.GetUtcNow().AddSeconds(seconds);
        Transition(state, message);
    }

    private void CheckTimeout(string message)
    {
        if (timeProvider.GetUtcNow() >= stateDeadline)
        {
            if (returnToAutoListingAfterCurrent)
                AbortFreshAutoListing(message);
            else
                RecoverInterface(message);
        }
    }

    // Only call for menu/read failures, never an unverified write. Unwind owned
    // retainer windows, then retry a full pass with fresh listings after a backoff.
    private void RecoverInterface(string reason)
    {
        if (returnToAutoListingAfterCurrent)
        {
            AbortFreshAutoListing(reason);
            return;
        }
        NoteRecoveryFailure(reason);
        if (!bellSession || !configuration.Current.AutomationEnabled || !configuration.Current.RepeatBellRuns)
        {
            Halt(reason);
            return;
        }
        if (consecutiveRecoveries >= MaximumConsecutiveRecoveries)
        {
            Halt($"{reason} Recovery has restarted the retainer pass {consecutiveRecoveries} times without " +
                 "finishing a retainer, so it has stopped instead of retrying the same step forever.");
            return;
        }
        sessionCancellation?.Cancel();
        marketTask = null;
        LastKnownFreeSaleSlots = null;
        requestedFillOnlyRun = false;
        fillOnlyRun = false;
        nextActionAt = timeProvider.GetUtcNow();
        WaitFor(AutomationState.RecoveringRetainerInterface, $"{reason} Returning to the bell to retry.", 60);
        log.Add(AutomationLogLevel.Warning, detail);
    }

    private const int MaximumConsecutiveRecoveries = 4;

    // Only a deliberate Start forgets a bad row. Interface recovery replays the
    // pass through the same session reset, so clearing this there would wipe the
    // very record that stops the failing listing being reopened again.
    private void ForgetProblemListings()
    {
        listingFailureCounts.Clear();
        skippedProblemListings.Clear();
        consecutiveRecoveries = 0;
        lastWriteFailure = null;
    }

    // Recovery returns to the bell and replays the pass, so without this a listing
    // that always fails the same step is reopened and re-read indefinitely. Drop the
    // row after a second failure and let the rest of the run continue.
    private void NoteRecoveryFailure(string reason)
    {
        consecutiveRecoveries++;
        if (queue.Count == 0 || currentIndex >= queue.Count)
            return;
        var listing = queue[currentIndex].Listing;
        var key = (listing.RetainerId, listing.Slot);
        var failures = listingFailureCounts.GetValueOrDefault(key) + 1;
        listingFailureCounts[key] = failures;
        if (failures < 2 || !skippedProblemListings.Add(key))
            return;
        log.Add(AutomationLogLevel.Error,
            $"SKIPPING {listing.ItemName} on {listing.RetainerName}: {reason} It failed {failures} times; " +
            "the rest of the run continues without it.");
    }

    // A rejected write leaves the price unchanged, so the next pass evaluates the
    // same listing and is rejected identically - reopening the price page forever
    // without ever updating it. Drop the row after a second rejection and say so.
    private void NoteListingWriteFailure(RetainerListing listing, string reason)
    {
        var key = (listing.RetainerId, listing.Slot);
        var failures = listingFailureCounts.GetValueOrDefault(key) + 1;
        listingFailureCounts[key] = failures;
        if (failures < 2 || !skippedProblemListings.Add(key))
            return;
        lastWriteFailure = $"{listing.ItemName} on {listing.RetainerName}: {reason}";
        log.Add(AutomationLogLevel.Error,
            $"SKIPPING {listing.ItemName} on {listing.RetainerName} for the rest of this run: the Adjust Price " +
            $"write was rejected {failures} times. {reason}");
    }

    private void PollInterfaceRecovery()
    {
        if (timeProvider.GetUtcNow() >= stateDeadline)
        {
            Halt("Could not recover the retainer interface within 60 seconds. Open the bell and press Start.");
            return;
        }
        if (!DelayElapsed())
            return;
        nextActionAt = timeProvider.GetUtcNow().AddSeconds(1);
        if (retainerListings.IsPriceEditorOpen)
        {
            retainerListings.CloseComparePrices();
            retainerListings.CancelPriceEditor();
        }
        else if (retainerListings.IsContextMenuOpen) retainerListings.CloseContextMenu();
        else if (retainerListings.IsBankOpen) retainerListings.CancelBankDialog();
        else if (retainerListings.IsSellListOpen) retainerListings.CloseSellList();
        else if (retainerListings.IsTalkOpen) retainerListings.AdvanceTalk();
        else if (retainerListings.IsRetainerMenuOpen) retainerListings.CloseRetainerMenu();
        else if (retainerListings.IsRetainerListOpen)
        {
            nextActionAt = timeProvider.GetUtcNow().AddMinutes(1);
            Transition(AutomationState.WaitingToRetryRetainers,
                $"Back at the bell. Retainer check retries at {nextActionAt.LocalDateTime:t}.");
        }
    }

    private bool DelayElapsed() => timeProvider.GetUtcNow() >= nextActionAt;

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
        AutomationState.WaitingAfterAutoListing or
        AutomationState.WaitingBeforeOpeningListing or
        AutomationState.WaitingBeforeOpeningPriceEditor or
        AutomationState.WaitingForPriceEditorStable or
        AutomationState.WaitingBeforeMarketRequest or
        AutomationState.WaitingBeforeCommit or
        AutomationState.WaitingAfterCommit or
        AutomationState.WaitingBeforeClosingSellList or
        AutomationState.WaitingBeforeGilCollection or
        AutomationState.WaitingBeforeGilAmount or
        AutomationState.WaitingBeforeGilConfirmation or
        AutomationState.WaitingBeforeClosingRetainer or
        AutomationState.WaitingForScheduledRun or
        AutomationState.WaitingToRetryRetainers;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
    }
}
