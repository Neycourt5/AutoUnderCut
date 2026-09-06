using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public enum ProcurementState
{
    Idle,
    ScanningUniversalis,
    PlanReady,
    WaitingBeforeWorldTravel,
    WaitingForWorld,
    WaitingAfterWorldArrival,
    WaitingForMarketBoardTravel,
    FindingMarketBoard,
    MovingToMarketBoard,
    WaitingForMarketBoard,
    WaitingAfterMarketBoardOpen,
    WaitingForStockHuntListings,
    AwaitingManualReview,
    WaitingForListings,
    WaitingForPurchase,
    WaitingBeforeHomeTravel,
    WaitingForHomeWorld,
    WaitingAfterHomeArrival,
    WaitingForSummoningBellTravel,
    FindingSummoningBell,
    MovingToSummoningBell,
    WaitingForSummoningBell,
    Completed,
    Halted,
    Faulted,
}

public enum ProcurementRunMode
{
    None,
    AutomaticPurchase,
    GuidedReview,
}

public sealed record ProcurementStatus(
    ProcurementState State,
    string Detail,
    int CurrentOrder,
    int TotalOrders,
    uint GilSpent,
    DateTimeOffset? NextAutomaticScan);

public sealed class ProcurementController : IDisposable
{
    private static readonly string[] NorthAmericaAndOceaniaWorlds =
    [
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
        "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph",
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",
    ];

    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly ICommandManager commandManager;
    private readonly IRetainerListingService retainerListings;
    private readonly IUniversalisService universalis;
    private readonly IProcurementPlannerService planner;
    private readonly IMarketPurchaseService market;
    private readonly IVnavmeshService vnavmesh;
    private readonly ILifestreamService lifestream;
    private readonly ITaskbarAttentionService taskbarAttention;
    private readonly ProcurementLedger ledger;
    private readonly AutomationController repricing;
    private readonly ConfigurationService configuration;
    private readonly AutomationLog log;
    private readonly TimeProvider timeProvider;

    private CancellationTokenSource? cancellation;
    private Task<IReadOnlyList<ProcurementMarketItem>>? scanTask;
    private List<IGrouping<string, ProcurementOrder>> worldGroups = [];
    private List<string> stockHuntWorlds = [];
    private List<ProcurementRule> stockHuntRules = [];
    private readonly List<ProcurementMarketListing> stockHuntListings = [];
    private ProcurementOrder? currentOrder;
    private LivePurchaseListing? currentLiveListing;
    private ProcurementRule? currentStockHuntRule;
    private DateTimeOffset deadline;
    private DateTimeOffset nextActionAt;
    private DateTimeOffset nextAutomaticScan;
    private DateTimeOffset nextLiveStockHunt;
    private int worldIndex;
    private int orderIndex;
    private int inventoryBefore;
    private int lastScannedFreeSaleSlots = -1;
    private ProcurementRunMode runAfterScan;
    private ProcurementRunMode activeRunMode;
    private string homeWorld = string.Empty;
    private string detail = "Procurement is idle.";
    private uint gilSpent;
    private int localTravelAttempts;
    private int stockHuntWorldIndex;
    private int stockHuntRuleIndex;
    private bool stockHuntScanning;
    private bool localTravelObservedBusy;
    private int listingRequestAttempts;
    private int successfulLiveScans;
    private int failedLiveScans;
    private int confirmedPurchases;
    private int skippedPurchases;
    private string routeOutcome = string.Empty;

    public ProcurementController(
        IFramework framework,
        IPlayerState playerState,
        ICommandManager commandManager,
        IRetainerListingService retainerListings,
        IUniversalisService universalis,
        IProcurementPlannerService planner,
        IMarketPurchaseService market,
        IVnavmeshService vnavmesh,
        ILifestreamService lifestream,
        ITaskbarAttentionService taskbarAttention,
        ProcurementLedger ledger,
        AutomationController repricing,
        ConfigurationService configuration,
        AutomationLog log,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.playerState = playerState;
        this.commandManager = commandManager;
        this.retainerListings = retainerListings;
        this.universalis = universalis;
        this.planner = planner;
        this.market = market;
        this.vnavmesh = vnavmesh;
        this.lifestream = lifestream;
        this.taskbarAttention = taskbarAttention;
        this.ledger = ledger;
        this.repricing = repricing;
        this.configuration = configuration;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        nextAutomaticScan = this.timeProvider.GetUtcNow().AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        nextLiveStockHunt = this.timeProvider.GetUtcNow();
        framework.Update += OnFrameworkUpdate;
    }

    public ProcurementState State { get; private set; } = ProcurementState.Idle;
    public ProcurementPlan Plan { get; private set; } = ProcurementPlan.Empty;
    public event Action? GuidedReviewRequested;
    public Func<bool>? IsStartBlocked { get; set; }
    public bool IsActive => State is not (ProcurementState.Idle or ProcurementState.PlanReady or ProcurementState.Completed or ProcurementState.Halted or ProcurementState.Faulted);
    public bool IsGuidedReviewPending => State == ProcurementState.AwaitingManualReview;
    public string CurrentGuidedWorld => IsGuidedReviewPending ? WorldName : string.Empty;
    public int CurrentGuidedWorldNumber => IsGuidedReviewPending ? worldIndex + 1 : 0;
    public int GuidedWorldCount => worldGroups.Count;
    public IReadOnlyList<ProcurementOrder> CurrentGuidedWorldOrders =>
        IsGuidedReviewPending && worldIndex < worldGroups.Count ? worldGroups[worldIndex].ToArray() : [];
    public ProcurementStatus Status => new(
        State, detail, Math.Min(orderIndex + 1, Plan.Orders.Count), Plan.Orders.Count, gilSpent,
        configuration.Current.AutomaticProcurementEnabled ? nextAutomaticScan : null);

    public void ScanNow() => StartScan(ProcurementRunMode.None);

    public void RunNow()
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        if (Plan.Orders.Count == 0)
            StartScan(ProcurementRunMode.AutomaticPurchase);
        else
            BeginExecution(ProcurementRunMode.AutomaticPurchase);
    }

    public void RunGuidedNow() => StartScan(ProcurementRunMode.GuidedReview);

    public void RunLiveStockHuntNow() => StartLiveStockHunt();

    public void CompleteGuidedWorldReview()
    {
        if (State != ProcurementState.AwaitingManualReview)
            return;
        taskbarAttention.StopFlashing();
        market.CloseMarketBoard();
        worldIndex++;
        orderIndex = 0;
        TravelToCurrentWorld();
    }

    public void Halt(string reason = "Procurement stopped by user.")
    {
        cancellation?.Cancel();
        scanTask = null;
        vnavmesh.Stop();
        if (activeRunMode != ProcurementRunMode.None)
            lifestream.Abort();
        taskbarAttention.StopFlashing();
        stockHuntScanning = false;
        activeRunMode = ProcurementRunMode.None;
        State = ProcurementState.Halted;
        detail = reason;
        log.Add(AutomationLogLevel.Warning, reason);
    }

    private void StartScan(ProcurementRunMode mode)
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        stockHuntScanning = false;
        var dataCenter = universalis.ResolveDataCenter(configuration.Current.ProcurementDataCenter);
        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            Halt("Could not determine a Universalis data center. Set it in the Procurement tab.");
            return;
        }
        if (configuration.Current.ProcurementRules.Count == 0)
        {
            Halt("No procurement items are configured. Add the favorite defaults in the Procurement tab.");
            return;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        runAfterScan = mode;
        scanTask = universalis.ScanAsync(configuration.Current.ProcurementRules, dataCenter, cancellation.Token);
        State = ProcurementState.ScanningUniversalis;
        detail = $"Scanning {dataCenter} on Universalis.";
        log.Add(AutomationLogLevel.Information, detail);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            cancellation?.Cancel();
            scanTask = null;
            if (activeRunMode != ProcurementRunMode.None)
                lifestream.Abort();
            activeRunMode = ProcurementRunMode.None;
            stockHuntScanning = false;
            State = ProcurementState.Faulted;
            detail = ex.Message;
            vnavmesh.Stop();
            taskbarAttention.StopFlashing();
            log.Add(AutomationLogLevel.Error, $"Procurement faulted: {ex}");
        }
    }

    private void Tick()
    {
        if (State == ProcurementState.ScanningUniversalis)
        {
            PollScan();
            return;
        }

        if (State is ProcurementState.Idle or ProcurementState.Completed or ProcurementState.PlanReady)
        {
            TryAutomaticStart();
            return;
        }
        if (State is ProcurementState.Faulted or ProcurementState.Halted)
            return;

        // Cross-DC travel intentionally passes through character selection.
        // Other steps must wait for a loaded character before touching game UI.
        if (!playerState.IsLoaded && State is not (ProcurementState.WaitingForWorld or ProcurementState.WaitingForHomeWorld))
        {
            CheckTimeout("The character did not finish loading before the route timed out.");
            return;
        }

        switch (State)
        {
            case ProcurementState.WaitingBeforeWorldTravel:
                PollWorldTravel(false);
                break;
            case ProcurementState.WaitingBeforeHomeTravel:
                PollWorldTravel(true);
                break;
            case ProcurementState.WaitingForWorld:
                if (IsOnWorld(WorldName) && !lifestream.IsBusy)
                    Delay(ProcurementState.WaitingAfterWorldArrival, "Destination world loaded; allowing the character to settle.", 8_000);
                else if (stockHuntScanning && timeProvider.GetUtcNow() >= deadline)
                    SkipStockHuntWorld($"LIVE TOUR timed out travelling to {WorldName}; skipping that world.");
                else
                    CheckTimeout($"Timed out travelling to {WorldName}.");
                break;
            case ProcurementState.WaitingAfterWorldArrival:
                if (DelayElapsed() && !lifestream.IsBusy) BeginLocalTravel(false);
                else CheckTimeout("Lifestream did not settle after world arrival.");
                break;
            case ProcurementState.WaitingForMarketBoardTravel:
                PollLocalTravel("Market Board", ProcurementState.FindingMarketBoard, false);
                break;
            case ProcurementState.FindingMarketBoard:
                FindAndApproach("Market Board", ProcurementState.MovingToMarketBoard, ProcurementState.WaitingForMarketBoard);
                break;
            case ProcurementState.MovingToMarketBoard:
                PollApproach("Market Board", ProcurementState.WaitingForMarketBoard);
                break;
            case ProcurementState.WaitingForMarketBoard:
                if (market.IsMarketBoardOpen)
                    Delay(ProcurementState.WaitingAfterMarketBoardOpen,
                        "Market Board opened; allowing item-search services to settle.", 3_000);
                else if (stockHuntScanning && timeProvider.GetUtcNow() >= deadline)
                    SkipStockHuntWorld($"LIVE TOUR could not open {WorldName}'s Market Board; skipping that world.");
                else
                    CheckTimeout("Timed out opening the Market Board.");
                break;
            case ProcurementState.WaitingAfterMarketBoardOpen:
                if (!market.IsMarketBoardOpen)
                {
                    if (stockHuntScanning)
                        SkipStockHuntWorld($"LIVE TOUR: {WorldName}'s Market Board closed before item search became ready; skipping that world.");
                    else
                        Halt("The Market Board closed before its item-search services became ready.");
                    break;
                }
                if (!DelayElapsed())
                    break;
                if (stockHuntScanning)
                    BeginStockHuntRuleScan();
                else if (activeRunMode == ProcurementRunMode.GuidedReview)
                    BeginGuidedReview();
                else
                    BeginCurrentOrder();
                break;
            case ProcurementState.WaitingForStockHuntListings:
                PollStockHuntListings();
                break;
            case ProcurementState.AwaitingManualReview:
                break;
            case ProcurementState.WaitingForListings:
                PollListings();
                break;
            case ProcurementState.WaitingForPurchase:
                PollPurchase();
                break;
            case ProcurementState.WaitingForHomeWorld:
                if (IsOnWorld(homeWorld) && !lifestream.IsBusy)
                    Delay(ProcurementState.WaitingAfterHomeArrival, "Home world loaded; allowing the character to settle.", 8_000);
                else
                    CheckTimeout($"Timed out returning to {homeWorld}.");
                break;
            case ProcurementState.WaitingAfterHomeArrival:
                if (DelayElapsed() && !lifestream.IsBusy) BeginLocalTravel(true);
                else CheckTimeout("Lifestream did not settle after returning home.");
                break;
            case ProcurementState.WaitingForSummoningBellTravel:
                PollLocalTravel("Summoning Bell", ProcurementState.FindingSummoningBell, true);
                break;
            case ProcurementState.FindingSummoningBell:
                FindAndApproach("Summoning Bell", ProcurementState.MovingToSummoningBell, ProcurementState.WaitingForSummoningBell);
                break;
            case ProcurementState.MovingToSummoningBell:
                PollApproach("Summoning Bell", ProcurementState.WaitingForSummoningBell);
                break;
            case ProcurementState.WaitingForSummoningBell:
                if (retainerListings.IsRetainerListOpen)
                {
                    var message = activeRunMode == ProcurementRunMode.GuidedReview
                        ? "Guided route finished and returned home; starting the normal retainer pass. Manually purchased bag items remain under your control."
                        : !string.IsNullOrEmpty(routeOutcome)
                            ? $"{routeOutcome} Returned home to the summoning bell."
                            : $"Shopping finished: {confirmedPurchases} confirmed purchase(s), {skippedPurchases} skipped order(s), {gilSpent:N0} gil spent. Returned home to the summoning bell.";
                    Complete(message);
                    // AutomationController observes the bell first in the framework
                    // update order and may already have started this pass.
                    if (!repricing.IsActive)
                        repricing.StartNow();
                }
                else
                    CheckTimeout("Timed out opening the summoning-bell retainer list.");
                break;
        }
    }

    private void PollScan()
    {
        if (scanTask is null || !scanTask.IsCompleted)
            return;
        if (scanTask.IsCanceled || scanTask.IsFaulted)
        {
            Halt(scanTask.Exception?.GetBaseException().Message ?? "Universalis scan was cancelled.");
            return;
        }
        var config = configuration.Current;
        var freeSaleSlots = repricing.LastKnownFreeSaleSlots ?? config.ProcurementTargetSaleSlots;
        var plannedSaleSlots = Math.Min(freeSaleSlots, config.ProcurementTargetSaleSlots);
        var freeInventorySlots = Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve);
        Plan = planner.BuildPlan(new(
            scanTask.Result,
            config.ProcurementRules,
            Math.Min(config.ProcurementBudget, market.Gil),
            plannedSaleSlots,
            freeInventorySlots,
            config.ProcurementMinimumRoiPercent,
            config.ProcurementMinimumProfitPerUnit));
        lastScannedFreeSaleSlots = plannedSaleSlots;
        scanTask = null;
        nextAutomaticScan = timeProvider.GetUtcNow().AddMinutes(config.ProcurementIntervalMinutes);
        State = ProcurementState.PlanReady;
        detail = Plan.Orders.Count == 0
            ? "No deals passed the volume, margin, budget, bag-slot, and sale-slot guards."
            : $"Plan ready: {Plan.Orders.Count} stack(s), {Plan.TotalCost:N0} gil, about {Plan.ExpectedProfit:N0} gil expected profit.";
        log.Add(AutomationLogLevel.Information, detail);
        if (runAfterScan != ProcurementRunMode.None && Plan.Orders.Count > 0)
            BeginExecution(runAfterScan);
    }

    private void StartLiveStockHunt()
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        if (!configuration.Current.AllowAutomaticPurchases)
        {
            State = ProcurementState.Halted;
            detail = "Arm automatic market-board purchases before starting the live all-world stock hunt.";
            log.Add(AutomationLogLevel.Warning, detail);
            return;
        }
        if (!playerState.IsLoaded)
        {
            Halt("The character is not fully loaded.");
            return;
        }
        if (!lifestream.IsAvailable || lifestream.IsBusy)
        {
            Halt("Lifestream must be enabled and idle before starting a live tour.");
            return;
        }
        if (repricing.LastKnownFreeSaleSlots is not > 0)
        {
            Halt("The live all-world stock hunt needs at least one confirmed empty retainer sale slot. Run the all-retainer bell pass first.");
            return;
        }

        if (!TryGetHomeWorld(out homeWorld))
        {
            Halt("Could not determine the character's home world.");
            return;
        }
        stockHuntRules = configuration.Current.ProcurementRules
            .Where(x => x.Enabled && x.ItemId != 0 && x.RequireHighQuality)
            .Where(x => market.GetInventoryCount(x.ItemId, true) < configuration.Current.LiveWorldStockThresholdPerItem)
            .DistinctBy(x => x.ItemId)
            .ToList();
        if (stockHuntRules.Count == 0)
        {
            State = ProcurementState.Completed;
            detail = $"No curated HQ item is below the live-tour threshold of {configuration.Current.LiveWorldStockThresholdPerItem:N0}.";
            log.Add(AutomationLogLevel.Information, detail);
            return;
        }

        ledger.ClearBagStockQueue();
        repricing.Halt("Paused while the live all-world stock hunt runs.");
        market.CloseRetainerList();
        activeRunMode = ProcurementRunMode.AutomaticPurchase;
        runAfterScan = ProcurementRunMode.None;
        Plan = ProcurementPlan.Empty;
        gilSpent = 0;
        confirmedPurchases = 0;
        skippedPurchases = 0;
        successfulLiveScans = 0;
        failedLiveScans = 0;
        routeOutcome = string.Empty;
        stockHuntListings.Clear();
        // Scan the home world last. Its prices are the resale anchor for every
        // prospective deal, so they should be the freshest data in the tour
        // when the guarded purchase plan is built.
        stockHuntWorlds = NorthAmericaAndOceaniaWorlds
            .Where(x => !string.Equals(x, homeWorld, StringComparison.OrdinalIgnoreCase))
            .Append(homeWorld)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        stockHuntWorldIndex = 0;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        stockHuntScanning = true;
        nextLiveStockHunt = timeProvider.GetUtcNow().AddMinutes(configuration.Current.LiveWorldStockHuntCooldownMinutes);
        detail = $"Starting live HQ stock hunt for {stockHuntRules.Count} low-stock item(s) across {stockHuntWorlds.Count} NA/Oceania worlds.";
        log.Add(AutomationLogLevel.Information, detail);
        TravelToCurrentWorld();
    }

    private void BeginExecution(ProcurementRunMode mode)
    {
        if (IsStartBlocked?.Invoke() == true)
            return;
        if (mode == ProcurementRunMode.AutomaticPurchase && !configuration.Current.AllowAutomaticPurchases)
        {
            State = ProcurementState.PlanReady;
            detail = "Purchase writes are disarmed; review the plan and arm them before running.";
            return;
        }
        if (Plan.Orders.Count == 0 || market.Gil < Plan.TotalCost)
        {
            Halt("The procurement plan is empty or no longer fits the available gil balance.");
            return;
        }
        if (!TryGetHomeWorld(out homeWorld))
        {
            Halt("Could not determine the character's home world.");
            return;
        }
        if (!lifestream.IsAvailable || lifestream.IsBusy)
        {
            Halt("Lifestream must be enabled and idle before starting a route.");
            return;
        }

        // Purchased stock owns its landed-cost ledger entries. Drop an older bag-refill
        // queue first so the two sources cannot merge under the same item key.
        ledger.ClearBagStockQueue();
        repricing.Halt("Paused while procurement runs.");
        activeRunMode = mode;
        runAfterScan = ProcurementRunMode.None;
        market.CloseRetainerList();
        var orderedWorlds = Plan.Orders.GroupBy(x => x.WorldName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Sum(y => (long)y.ExpectedProfit));
        worldGroups = (mode == ProcurementRunMode.GuidedReview
                ? orderedWorlds.Take(configuration.Current.GuidedTourMaximumWorlds)
                : orderedWorlds)
            .ToList();
        worldIndex = 0;
        orderIndex = 0;
        gilSpent = 0;
        confirmedPurchases = 0;
        skippedPurchases = 0;
        routeOutcome = string.Empty;
        TravelToCurrentWorld();
    }

    private void BeginGuidedReview()
    {
        if (worldIndex >= worldGroups.Count)
        {
            ReturnHome();
            return;
        }

        State = ProcurementState.AwaitingManualReview;
        detail = $"Reviewing {worldGroups[worldIndex].Count()} expected deal(s) on {WorldName}; waiting for Done / Next.";
        taskbarAttention.FlashUntilForeground();
        GuidedReviewRequested?.Invoke();
        log.Add(AutomationLogLevel.Information,
            $"GUIDED ROUTE READY on {WorldName}: {worldGroups[worldIndex].Count()} expected listing(s). " +
            "Review the live board, then choose Done here / Next world.");
    }

    private string WorldName => stockHuntScanning
        ? stockHuntWorldIndex < stockHuntWorlds.Count ? stockHuntWorlds[stockHuntWorldIndex] : string.Empty
        : worldIndex < worldGroups.Count ? worldGroups[worldIndex].Key : string.Empty;

    private void TravelToCurrentWorld()
    {
        market.CloseMarketBoard();
        if (stockHuntScanning && stockHuntWorldIndex >= stockHuntWorlds.Count)
        {
            CompleteStockHuntScan();
            return;
        }
        if (!stockHuntScanning && worldIndex >= worldGroups.Count)
        {
            ReturnHome();
            return;
        }
        if (IsOnWorld(WorldName))
        {
            Delay(ProcurementState.WaitingAfterWorldArrival, $"Preparing to visit {WorldName}'s market board.", 3_000);
            return;
        }
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        Wait(ProcurementState.WaitingBeforeWorldTravel, $"Closing market windows before travelling to {WorldName}.", 60);
    }

    private void PollWorldTravel(bool returningHome)
    {
        if (!DelayElapsed() || lifestream.IsBusy)
        {
            CheckTimeout("Lifestream did not become idle before the next world transfer.");
            return;
        }
        if (!lifestream.IsAvailable)
        {
            Halt("Lifestream is unavailable; the route cannot continue.");
            return;
        }
        var destination = returningHome ? homeWorld : WorldName;
        // The public Lifestream IPC can acknowledge a world change without
        // beginning travel on some versions. The literal chat command is the
        // same path the user has confirmed works reliably.
        var accepted = commandManager.ProcessCommand($"/li {destination}");
        if (!accepted)
        {
            if (stockHuntScanning)
            {
                SkipStockHuntWorld($"Lifestream could not visit {WorldName}; skipping that world.");
                return;
            }
            Halt("The literal /li world-travel command was not accepted. Check that Lifestream is enabled, then retry the route.");
            return;
        }
        Wait(returningHome ? ProcurementState.WaitingForHomeWorld : ProcurementState.WaitingForWorld,
            $"Travelling to {destination} with Lifestream.", 600);
    }

    private void BeginLocalTravel(bool returningHome)
    {
        localTravelAttempts = 0;
        localTravelObservedBusy = false;
        nextActionAt = timeProvider.GetUtcNow();
        Wait(
            returningHome ? ProcurementState.WaitingForSummoningBellTravel : ProcurementState.WaitingForMarketBoardTravel,
            returningHome
                ? "Waiting for Lifestream to take us to the home market area."
                : $"Waiting for Lifestream to take us to {WorldName}'s market area.",
            75);
    }

    private void BeginStockHuntRuleScan()
    {
        if (!stockHuntScanning)
            return;
        if (stockHuntRuleIndex >= stockHuntRules.Count)
        {
            FinishStockHuntWorld();
            return;
        }

        currentStockHuntRule = stockHuntRules[stockHuntRuleIndex];
        listingRequestAttempts = 1;
        if (!market.RequestListings(currentStockHuntRule.ItemId))
        {
            nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            Wait(ProcurementState.WaitingForStockHuntListings,
                $"Waiting to scan {currentStockHuntRule.ItemName} on {WorldName}.", 30);
            return;
        }
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        Wait(ProcurementState.WaitingForStockHuntListings,
            $"Reading live {currentStockHuntRule.ItemName} listings on {WorldName}.", 30);
    }

    private void PollStockHuntListings()
    {
        if (currentStockHuntRule is null)
        {
            if (DelayElapsed())
                BeginStockHuntRuleScan();
            return;
        }

        if (!market.AreListingsReady(currentStockHuntRule.ItemId))
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                if (RetryListingRequest(currentStockHuntRule.ItemName))
                    return;
                failedLiveScans++;
                market.ResetListingRequest();
                log.Add(AutomationLogLevel.Warning,
                    $"LIVE TOUR SKIPPED {currentStockHuntRule.ItemName} on {WorldName}: market request timed out.");
                AdvanceStockHuntRule();
                return;
            }
            if (DelayElapsed())
            {
                market.RequestListings(currentStockHuntRule.ItemId);
                nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            }
            return;
        }

        var live = market.ReadLiveListings(currentStockHuntRule.ItemId)
            .Where(x => x.IsHighQuality)
            .ToArray();
        successfulLiveScans++;
        foreach (var listing in live)
        {
            stockHuntListings.Add(new(
                listing.ItemId,
                listing.ListingId,
                listing.RetainerId,
                WorldName,
                0,
                listing.PricePerUnit,
                listing.Quantity,
                listing.IsHighQuality));
        }
        log.Add(AutomationLogLevel.Information,
            $"LIVE TOUR {WorldName}: {currentStockHuntRule.ItemName} returned {live.Length} eligible HQ listing(s).");
        AdvanceStockHuntRule();
    }

    private void AdvanceStockHuntRule()
    {
        market.ResetListingRequest();
        stockHuntRuleIndex++;
        currentStockHuntRule = null;
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        State = ProcurementState.WaitingForStockHuntListings;
        detail = $"Waiting before the next live scan on {WorldName}.";
    }

    private void FinishStockHuntWorld()
    {
        var completedWorld = WorldName;
        market.CloseMarketBoard();
        stockHuntWorldIndex++;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        log.Add(AutomationLogLevel.Information,
            $"LIVE TOUR finished {completedWorld} ({stockHuntWorldIndex}/{stockHuntWorlds.Count}).");
        TravelToCurrentWorld();
    }

    private void SkipStockHuntWorld(string reason)
    {
        log.Add(AutomationLogLevel.Warning, reason);
        lifestream.Abort();
        vnavmesh.Stop();
        market.CloseMarketBoard();
        stockHuntWorldIndex++;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        TravelToCurrentWorld();
    }

    private void CompleteStockHuntScan()
    {
        stockHuntScanning = false;
        market.CloseMarketBoard();
        var markets = stockHuntRules.Select(rule => new ProcurementMarketItem(
            rule.ItemId,
            rule.ItemName,
            stockHuntListings
                .Where(x => x.ItemId == rule.ItemId)
                .DistinctBy(x => (x.WorldName, x.ListingId, x.RetainerId))
                .ToArray(),
            [])).ToArray();
        var config = configuration.Current;
        var freeSaleSlots = repricing.LastKnownFreeSaleSlots ?? 0;
        Plan = planner.BuildLiveMarketPlan(new(
            markets,
            stockHuntRules,
            homeWorld,
            retainerListings.OwnedRetainerIds,
            Math.Min(config.ProcurementBudget, market.Gil),
            Math.Min(freeSaleSlots, config.ProcurementTargetSaleSlots),
            Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve),
            config.ProcurementMinimumRoiPercent,
            config.ProcurementMinimumProfitPerUnit));
        detail = Plan.Orders.Count == 0
            ? $"Live tour checked {stockHuntWorlds.Count} worlds; no listing beat the live {homeWorld} resale floor and safety guards."
            : $"Live tour found {Plan.Orders.Count} guarded buy(s), costing {Plan.TotalCost:N0} gil with about {Plan.ExpectedProfit:N0} gil expected profit.";
        log.Add(AutomationLogLevel.Information, detail);
        if (Plan.Orders.Count == 0)
        {
            var homeItems = stockHuntListings
                .Where(x => string.Equals(x.WorldName, homeWorld, StringComparison.OrdinalIgnoreCase) &&
                            x.Quantity > 0 && x.PricePerUnit > 0 &&
                            !retainerListings.OwnedRetainerIds.Contains(x.RetainerId))
                .Select(x => x.ItemId).Distinct().Count();
            routeOutcome = successfulLiveScans == 0
                ? "No purchases: every live item search failed; no usable market data was received."
                : homeItems == 0
                    ? $"No purchases: no usable HQ resale prices were received from {homeWorld}. The purchase plan could not be built."
                    : $"No purchases: no listing passed the resale, profit, budget, and capacity guards. {successfulLiveScans} item search(es) completed; {failedLiveScans} timed out.";
            log.Add(AutomationLogLevel.Warning, routeOutcome);
            ReturnHome();
            return;
        }
        BeginExecution(ProcurementRunMode.AutomaticPurchase);
    }

    private void BeginCurrentOrder()
    {
        if (worldIndex >= worldGroups.Count)
        {
            ReturnHome();
            return;
        }
        var orders = worldGroups[worldIndex].ToArray();
        if (orderIndex >= orders.Length)
        {
            worldIndex++;
            orderIndex = 0;
            TravelToCurrentWorld();
            return;
        }
        currentOrder = orders[orderIndex];
        listingRequestAttempts = 1;
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        if (!market.RequestListings(currentOrder.ItemId))
        {
            nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            Wait(ProcurementState.WaitingForListings, $"Waiting to request {currentOrder.ItemName}.", 30);
            return;
        }
        Wait(ProcurementState.WaitingForListings, $"Revalidating live prices for {currentOrder.ItemName}.", 30);
    }

    private void PollListings()
    {
        if (!configuration.Current.AllowAutomaticPurchases)
        {
            Halt("Automatic purchases were disarmed; the route stopped before submitting another purchase.");
            return;
        }
        if (!IsOnWorld(WorldName) || !market.IsMarketBoardOpen || lifestream.IsBusy)
        {
            Halt("The destination world or Market Board changed before purchase validation.");
            return;
        }
        if (currentOrder is null)
        {
            if (!DelayElapsed())
                return;
            BeginCurrentOrder();
            return;
        }
        if (!market.AreListingsReady(currentOrder.ItemId))
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                if (RetryListingRequest(currentOrder.ItemName))
                    return;
                market.ResetListingRequest();
                SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the live market request timed out.");
                return;
            }
            if (DelayElapsed())
            {
                market.RequestListings(currentOrder.ItemId);
                nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            }
            return;
        }
        if (!market.TrySelectLiveListing(currentOrder, retainerListings.OwnedRetainerIds, out var live) || live is null)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the Universalis deal was gone or exceeded the live ceiling.");
            return;
        }

        var totalCost = GetPurchaseCost(live);
        var expectedNetProceeds = decimal.Floor(currentOrder.TargetSalePrice * 0.95m) * live.Quantity;
        if (expectedNetProceeds < totalCost * (1m + configuration.Current.ProcurementMinimumRoiPercent / 100m) ||
            expectedNetProceeds - totalCost < (decimal)configuration.Current.ProcurementMinimumProfitPerUnit * live.Quantity)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the live buyer tax no longer meets the profit guards.");
            return;
        }
        if (market.FreeInventorySlots <= configuration.Current.ProcurementInventoryReserve)
        {
            Halt("The inventory reserve was reached; no further purchases will be submitted.");
            return;
        }
        var remainingBudget = configuration.Current.ProcurementBudget > gilSpent
            ? configuration.Current.ProcurementBudget - gilSpent
            : 0;
        if (totalCost > remainingBudget || totalCost > market.Gil)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: budget or gil balance changed.");
            return;
        }
        inventoryBefore = market.GetInventoryCount(live.ItemId, live.IsHighQuality);
        currentLiveListing = live;
        if (!market.SubmitPurchase(live))
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the live purchase request was rejected.");
            return;
        }
        Wait(ProcurementState.WaitingForPurchase, $"Waiting for {currentOrder.ItemName} purchase confirmation.", 10);
    }

    private void PollPurchase()
    {
        if (currentOrder is null || currentLiveListing is null)
        {
            SkipCurrentOrder("Purchase state was lost; skipping the order.");
            return;
        }
        var count = market.GetInventoryCount(currentLiveListing.ItemId, currentLiveListing.IsHighQuality);
        if (count < inventoryBefore + currentLiveListing.Quantity)
        {
            if (timeProvider.GetUtcNow() < deadline)
                return;
            Halt($"PURCHASE OUTCOME UNKNOWN for {currentOrder.ItemName}: the game accepted the request, " +
                 "but inventory did not confirm it before the timeout. Procurement stopped to prevent a duplicate buy.");
            return;
        }

        var actual = currentOrder with
        {
            ListingId = currentLiveListing.ListingId,
            RetainerId = currentLiveListing.RetainerId,
            PricePerUnit = currentLiveListing.PricePerUnit,
            Quantity = currentLiveListing.Quantity,
        };
        var stackSize = configuration.Current.ProcurementRules.FirstOrDefault(x => x.ItemId == actual.ItemId)?.TargetStackSize ?? 99;
        ledger.RecordPurchase(actual, stackSize);
        var pricingRule = configuration.Current.PerItemRules.TryGetValue(actual.ItemId, out var existingRule)
            ? existingRule
            : configuration.Current.GlobalRule.Clone();
        // Use the server-provided buyer tax from the exact live listing. Store
        // the rounded-up landed unit cost so repricing cannot sell below what
        // this particular stack actually cost.
        var purchaseCost = GetPurchaseCost(currentLiveListing);
        var landedCostPerUnit = (uint)Math.Min(
            PricingStrategyService.MaximumListingPrice,
            (purchaseCost + actual.Quantity - 1) / actual.Quantity);
        pricingRule.CostBasis = Math.Max(pricingRule.CostBasis, landedCostPerUnit);
        pricingRule.MinimumMarginPercent = Math.Max(
            pricingRule.MinimumMarginPercent, configuration.Current.ProcurementMinimumRoiPercent);
        configuration.Current.PerItemRules[actual.ItemId] = pricingRule;
        configuration.Save();
        gilSpent += (uint)Math.Min(purchaseCost, uint.MaxValue - gilSpent);
        confirmedPurchases++;
        log.Add(AutomationLogLevel.Information,
            $"PURCHASED {actual.ItemName} x{actual.Quantity} on {actual.WorldName} at {actual.PricePerUnit:N0} gil each; " +
            $"buyer tax {currentLiveListing.TotalTax:N0} gil, tracked landed cost {landedCostPerUnit:N0} gil each.");
        AdvanceOrder();
    }

    private void SkipCurrentOrder(string message)
    {
        skippedPurchases++;
        log.Add(AutomationLogLevel.Warning, message);
        AdvanceOrder();
    }

    private bool RetryListingRequest(string itemName)
    {
        if (listingRequestAttempts >= 3)
            return false;
        listingRequestAttempts++;
        market.ResetListingRequest();
        nextActionAt = timeProvider.GetUtcNow().AddSeconds(2);
        deadline = nextActionAt.AddSeconds(30);
        detail = $"Retrying the live search for {itemName} on {WorldName} (attempt {listingRequestAttempts}/3). No purchase has been submitted for this order.";
        log.Add(AutomationLogLevel.Warning, detail);
        return true;
    }

    private void AdvanceOrder()
    {
        // The server can leave the just-purchased row in the current proxy until
        // a fresh request. This is essential when consecutive plan entries are
        // the same item: never submit against a stale listing snapshot.
        market.ResetListingRequest();
        orderIndex++;
        currentOrder = null;
        currentLiveListing = null;
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        detail = "Waiting before the next live market request.";
        // Reuse the listings state as a throttled handoff; it will call BeginCurrentOrder.
        State = ProcurementState.WaitingForListings;
    }

    private static ulong GetPurchaseCost(LivePurchaseListing listing) =>
        (ulong)listing.PricePerUnit * listing.Quantity + listing.TotalTax;

    private void ReturnHome()
    {
        market.CloseMarketBoard();
        if (IsOnWorld(homeWorld))
        {
            Delay(ProcurementState.WaitingAfterHomeArrival, "Preparing to return to the summoning bell.", 3_000);
            return;
        }
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        Wait(ProcurementState.WaitingBeforeHomeTravel, $"Closing market windows before returning to {homeWorld}.", 60);
    }

    private void PollLocalTravel(string objectName, ProcurementState foundState, bool returningHome)
    {
        if (lifestream.IsBusy)
        {
            localTravelObservedBusy = true;
            detail = returningHome
                ? "Lifestream is moving to the home market area."
                : $"Lifestream is moving to {WorldName}'s market area.";
            if (stockHuntScanning && timeProvider.GetUtcNow() >= deadline)
                SkipStockHuntWorld($"LIVE TOUR timed out reaching {WorldName}'s {objectName}; skipping that world.");
            else
                CheckTimeout($"Lifestream did not finish travelling to the market area near {objectName}.");
            return;
        }

        // Always ask Lifestream to perform its complete `/li mb` route first.
        // A Market Board can already be present in the object table while the
        // character is still across the city; treating that as arrival was the
        // reason the route sometimes stopped in Limsa and fell through to vnav.
        if (localTravelAttempts == 0 && timeProvider.GetUtcNow() >= nextActionAt)
        {
            SubmitMarketTravelAttempt(objectName);
            return;
        }

        // Give Lifestream time to consume the chat command. Without this grace
        // period the next framework frame could see an already-loaded board in
        // the object table and start vnav before `/li mb` began moving us.
        if (!localTravelObservedBusy && timeProvider.GetUtcNow() < nextActionAt)
        {
            CheckTimeout($"Waiting for '{configuration.Current.MarketBoardTravelCommand}' to begin.");
            return;
        }

        if (market.FindNearest(objectName).HasValue)
        {
            State = foundState;
            detail = $"Found {objectName}; preparing to approach it.";
            deadline = timeProvider.GetUtcNow().AddSeconds(60);
            return;
        }

        if (timeProvider.GetUtcNow() < nextActionAt)
        {
            CheckTimeout($"Could not find {objectName} after travelling to the market area.");
            return;
        }

        if (localTravelAttempts >= 3)
        {
            if (stockHuntScanning)
            {
                SkipStockHuntWorld($"LIVE TOUR could not reach {WorldName}'s {objectName} after 3 attempts; skipping that world.");
                return;
            }
            Halt($"Could not reach a {objectName} after 3 Lifestream market-area attempts. " +
                 $"Check that '{configuration.Current.MarketBoardTravelCommand}' works in chat and that Lifestream is enabled.");
            return;
        }

        SubmitMarketTravelAttempt(objectName);
    }

    private void SubmitMarketTravelAttempt(string objectName)
    {
        localTravelObservedBusy = false;
        localTravelAttempts++;
        if (!TryExecuteMarketTravel())
        {
            nextActionAt = timeProvider.GetUtcNow().AddSeconds(4);
            detail = $"Lifestream did not accept '{configuration.Current.MarketBoardTravelCommand}' " +
                     $"attempt {localTravelAttempts}/3; waiting to retry.";
            log.Add(AutomationLogLevel.Warning, detail);
            return;
        }

        nextActionAt = timeProvider.GetUtcNow().AddSeconds(12);
        detail = $"Sent '{configuration.Current.MarketBoardTravelCommand}' " +
                 $"(attempt {localTravelAttempts}/3); waiting for {objectName} to load.";
        log.Add(AutomationLogLevel.Information, detail);
    }

    private bool TryExecuteMarketTravel()
    {
        var command = configuration.Current.MarketBoardTravelCommand.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return false;

        // Send the configured chat command literally. In particular, `/li mb`
        // performs Lifestream's complete local market-board route; invoking its
        // internal ExecuteCommand IPC did not consistently start that route.
        return commandManager.ProcessCommand(command);
    }

    private void FindAndApproach(string objectName, ProcurementState movingState, ProcurementState openedState)
    {
        if (ApproachExpired(objectName) || !DelayElapsed())
            return;
        if (openedState == ProcurementState.WaitingForMarketBoard && market.IsMarketBoardOpen)
        {
            Wait(openedState, "Market Board is already open.", 15);
            return;
        }
        if (openedState == ProcurementState.WaitingForSummoningBell && market.IsMarketBoardOpen)
        {
            market.CloseMarketBoard();
            nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            return;
        }
        var position = market.FindNearest(objectName);
        if (!position.HasValue)
        {
            if (stockHuntScanning && timeProvider.GetUtcNow() >= deadline)
                SkipStockHuntWorld($"LIVE TOUR could not find {WorldName}'s {objectName}; skipping that world.");
            else
                CheckTimeout($"Could not find a {objectName} before the timeout.");
            return;
        }
        if (market.DistanceTo(position.Value) <= 4.5f)
        {
            nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_000);
            if (market.InteractNearest(objectName))
                Wait(openedState, $"Opening {objectName}.", 15);
            return;
        }
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_000);
        if (!vnavmesh.IsReady || !vnavmesh.MoveTo(position.Value, 3f))
        {
            if (stockHuntScanning && timeProvider.GetUtcNow() >= deadline)
                SkipStockHuntWorld($"LIVE TOUR could not path to {WorldName}'s {objectName}; skipping that world.");
            else
                CheckTimeout("vnavmesh was unavailable or could not create a path.");
            return;
        }
        State = movingState;
        detail = $"Walking to {objectName} with vnavmesh.";
    }

    private void PollApproach(string objectName, ProcurementState openedState)
    {
        if (ApproachExpired(objectName) || !DelayElapsed())
            return;
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_000);
        var position = market.FindNearest(objectName);
        if (position.HasValue && market.DistanceTo(position.Value) <= 4.5f)
        {
            vnavmesh.Stop();
            if (market.InteractNearest(objectName))
                Wait(openedState, $"Opening {objectName}.", 15);
            return;
        }
        if (!vnavmesh.IsRunning && position.HasValue)
            vnavmesh.MoveTo(position.Value, 3f);
        if (stockHuntScanning && timeProvider.GetUtcNow() >= deadline)
            SkipStockHuntWorld($"LIVE TOUR timed out walking to {WorldName}'s {objectName}; skipping that world.");
        else
            CheckTimeout($"Timed out walking to {objectName}.");
    }

    private void TryAutomaticStart()
    {
        if (IsStartBlocked?.Invoke() == true || !configuration.Current.AllowAutomaticPurchases ||
            !configuration.Current.AutomaticProcurementEnabled || !retainerListings.IsRetainerListOpen ||
            repricing.IsActive)
            return;

        if (configuration.Current.LiveWorldStockHuntEnabled)
        {
            if (configuration.Current.AllowAutomaticPurchases &&
                repricing.LastKnownFreeSaleSlots is > 0 &&
                timeProvider.GetUtcNow() >= nextLiveStockHunt && HasLowCuratedStock())
                StartLiveStockHunt();

            // Live-market mode deliberately does not fall through to an automatic
            // Universalis plan. Universalis remains available through its manual
            // scan button, while unattended procurement uses only visible in-game
            // searches and revalidates every purchase against the selected listing.
            return;
        }

        // A completed retainer pass gives us an authoritative free-slot count. Scan
        // immediately when that capacity changes (a listing sold), otherwise use the
        // configured periodic interval while the character remains idle at the bell.
        var freeSaleSlots = repricing.LastKnownFreeSaleSlots;
        var newlyAvailableCapacity = freeSaleSlots is > 0 &&
                                     Math.Min(freeSaleSlots.Value, configuration.Current.ProcurementTargetSaleSlots) !=
                                     lastScannedFreeSaleSlots;
        if (!newlyAvailableCapacity && timeProvider.GetUtcNow() < nextAutomaticScan)
            return;
        nextAutomaticScan = timeProvider.GetUtcNow().AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        StartScan(ProcurementRunMode.AutomaticPurchase);
    }

    private bool HasLowCuratedStock() => configuration.Current.ProcurementRules
        .Where(x => x.Enabled && x.ItemId != 0 && x.RequireHighQuality)
        .Any(x => market.GetInventoryCount(x.ItemId, true) < configuration.Current.LiveWorldStockThresholdPerItem);

    private bool IsOnWorld(string world)
    {
        if (!playerState.IsLoaded || string.IsNullOrWhiteSpace(world))
            return false;

        try
        {
            return string.Equals(playerState.CurrentWorld.Value.Name.ToString(), world,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // RowRef.Value is temporarily unavailable while cross-DC travel is
            // loading. Treat that as "not arrived yet" and keep polling.
            return false;
        }
    }

    private bool TryGetHomeWorld(out string world)
    {
        world = string.Empty;
        if (!playerState.IsLoaded)
            return false;

        try
        {
            world = playerState.HomeWorld.Value.Name.ToString();
            return !string.IsNullOrWhiteSpace(world);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Complete(string message)
    {
        activeRunMode = ProcurementRunMode.None;
        taskbarAttention.StopFlashing();
        State = ProcurementState.Completed;
        detail = message;
        nextAutomaticScan = timeProvider.GetUtcNow().AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        log.Add(AutomationLogLevel.Information, message);
    }

    private void Wait(ProcurementState state, string message, int seconds)
    {
        State = state;
        detail = message;
        deadline = timeProvider.GetUtcNow().AddSeconds(seconds);
    }

    private void Delay(ProcurementState state, string message, int milliseconds)
    {
        State = state;
        detail = message;
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(milliseconds);
        deadline = nextActionAt.AddSeconds(60);
    }

    private bool ApproachExpired(string objectName)
    {
        if (timeProvider.GetUtcNow() < deadline)
            return false;
        if (stockHuntScanning)
            SkipStockHuntWorld($"LIVE TOUR timed out approaching or interacting with {WorldName}'s {objectName}.");
        else
            Halt($"Timed out approaching or interacting with {objectName}.");
        return true;
    }

    private void CheckTimeout(string message)
    {
        if (timeProvider.GetUtcNow() >= deadline)
            Halt(message);
    }

    private bool DelayElapsed() => timeProvider.GetUtcNow() >= nextActionAt;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (activeRunMode != ProcurementRunMode.None)
            lifestream.Abort();
        vnavmesh.Stop();
        taskbarAttention.StopFlashing();
    }
}
