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
    WaitingForWorld,
    WaitingAfterWorldArrival,
    WaitingForMarketBoardTravel,
    FindingMarketBoard,
    MovingToMarketBoard,
    WaitingForMarketBoard,
    WaitingForStockHuntListings,
    AwaitingManualReview,
    WaitingForListings,
    WaitingForPurchase,
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
        AutomationLog log)
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
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        nextLiveStockHunt = DateTimeOffset.UtcNow;
        framework.Update += OnFrameworkUpdate;
    }

    public ProcurementState State { get; private set; } = ProcurementState.Idle;
    public ProcurementPlan Plan { get; private set; } = ProcurementPlan.Empty;
    public event Action? GuidedReviewRequested;
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
        taskbarAttention.StopFlashing();
        stockHuntScanning = false;
        State = ProcurementState.Halted;
        detail = reason;
        log.Add(AutomationLogLevel.Warning, reason);
    }

    private void StartScan(ProcurementRunMode mode)
    {
        if (IsActive)
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

        if (State is ProcurementState.Idle or ProcurementState.Completed or ProcurementState.Halted or ProcurementState.PlanReady)
        {
            TryAutomaticStart();
            return;
        }
        if (State is ProcurementState.Faulted)
            return;

        switch (State)
        {
            case ProcurementState.WaitingForWorld:
                if (IsOnWorld(WorldName) && !lifestream.IsBusy)
                    Delay(ProcurementState.WaitingAfterWorldArrival, "Destination world loaded; allowing the character to settle.", 8_000);
                else if (stockHuntScanning && DateTimeOffset.UtcNow >= deadline)
                    SkipStockHuntWorld($"LIVE TOUR timed out travelling to {WorldName}; skipping that world.");
                else
                    CheckTimeout($"Timed out travelling to {WorldName}.");
                break;
            case ProcurementState.WaitingAfterWorldArrival:
                if (DelayElapsed() && !lifestream.IsBusy) BeginLocalTravel(false);
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
                {
                    if (stockHuntScanning)
                        BeginStockHuntRuleScan();
                    else if (activeRunMode == ProcurementRunMode.GuidedReview)
                        BeginGuidedReview();
                    else
                        BeginCurrentOrder();
                }
                else if (stockHuntScanning && DateTimeOffset.UtcNow >= deadline)
                    SkipStockHuntWorld($"LIVE TOUR could not open {WorldName}'s Market Board; skipping that world.");
                else
                    CheckTimeout("Timed out opening the Market Board.");
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
                        : "Purchasing finished; the retainer run will distribute and list purchased stacks.";
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
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(config.ProcurementIntervalMinutes);
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
        if (IsActive)
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
        if (repricing.LastKnownFreeSaleSlots is not > 0)
        {
            Halt("The live all-world stock hunt needs at least one confirmed empty retainer sale slot. Run the all-retainer bell pass first.");
            return;
        }

        homeWorld = playerState.HomeWorld.Value.Name.ToString();
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
        stockHuntListings.Clear();
        stockHuntWorlds = NorthAmericaAndOceaniaWorlds
            .Prepend(homeWorld)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        stockHuntWorldIndex = 0;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        stockHuntScanning = true;
        nextLiveStockHunt = DateTimeOffset.UtcNow.AddMinutes(configuration.Current.LiveWorldStockHuntCooldownMinutes);
        detail = $"Starting live HQ stock hunt for {stockHuntRules.Count} low-stock item(s) across {stockHuntWorlds.Count} NA/Oceania worlds.";
        log.Add(AutomationLogLevel.Information, detail);
        TravelToCurrentWorld();
    }

    private void BeginExecution(ProcurementRunMode mode)
    {
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
        homeWorld = playerState.IsLoaded ? playerState.HomeWorld.Value.Name.ToString() : string.Empty;
        if (string.IsNullOrWhiteSpace(homeWorld))
        {
            Halt("Could not determine the character's home world.");
            return;
        }

        // Purchased stock owns its landed-cost ledger entries. Drop an older bag-refill
        // queue first so the two sources cannot merge under the same item key.
        ledger.ClearBagStockQueue();
        repricing.Halt("Paused while procurement runs.");
        activeRunMode = mode;
        runAfterScan = ProcurementRunMode.None;
        market.CloseRetainerList();
        worldGroups = Plan.Orders.GroupBy(x => x.WorldName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Sum(y => (long)y.ExpectedProfit)).ToList();
        worldIndex = 0;
        orderIndex = 0;
        gilSpent = 0;
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
        var accepted = lifestream.IsAvailable
            ? lifestream.ChangeWorld(WorldName)
            : commandManager.ProcessCommand($"/li {WorldName}");
        if (!accepted)
        {
            if (stockHuntScanning)
            {
                SkipStockHuntWorld($"Lifestream could not visit {WorldName}; skipping that world.");
                return;
            }
            Halt("Lifestream was busy or did not accept the world-travel command. Try Run guarded purchase plan again.");
            return;
        }
        Wait(ProcurementState.WaitingForWorld, $"Travelling to {WorldName} with Lifestream.", 180);
    }

    private void BeginLocalTravel(bool returningHome)
    {
        localTravelAttempts = 0;
        nextActionAt = DateTimeOffset.UtcNow;
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
        if (!market.RequestListings(currentStockHuntRule.ItemId))
        {
            nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
            Wait(ProcurementState.WaitingForStockHuntListings,
                $"Waiting to scan {currentStockHuntRule.ItemName} on {WorldName}.", 12);
            return;
        }
        nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
        Wait(ProcurementState.WaitingForStockHuntListings,
            $"Reading live {currentStockHuntRule.ItemName} listings on {WorldName}.", 12);
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
            if (DateTimeOffset.UtcNow >= deadline)
            {
                log.Add(AutomationLogLevel.Warning,
                    $"LIVE TOUR SKIPPED {currentStockHuntRule.ItemName} on {WorldName}: market request timed out.");
                AdvanceStockHuntRule();
                return;
            }
            if (DelayElapsed())
            {
                market.RequestListings(currentStockHuntRule.ItemId);
                nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
            }
            return;
        }

        var live = market.ReadLiveListings(currentStockHuntRule.ItemId)
            .Where(x => x.IsHighQuality && x.Quantity <= Math.Max(1, currentStockHuntRule.TargetStackSize))
            .ToArray();
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
        stockHuntRuleIndex++;
        currentStockHuntRule = null;
        nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
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
        if (!market.RequestListings(currentOrder.ItemId))
        {
            nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
            Wait(ProcurementState.WaitingForListings, $"Waiting to request {currentOrder.ItemName}.", 10);
            return;
        }
        Wait(ProcurementState.WaitingForListings, $"Revalidating live prices for {currentOrder.ItemName}.", 12);
    }

    private void PollListings()
    {
        if (currentOrder is null)
        {
            if (!DelayElapsed())
                return;
            BeginCurrentOrder();
            return;
        }
        if (!market.AreListingsReady(currentOrder.ItemId))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the live market request timed out.");
                return;
            }
            if (DelayElapsed())
            {
                market.RequestListings(currentOrder.ItemId);
                nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
            }
            return;
        }
        if (!market.TrySelectLiveListing(currentOrder, out var live) || live is null)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the Universalis deal was gone or exceeded the live ceiling.");
            return;
        }

        var totalCost = (ulong)live.PricePerUnit * live.Quantity;
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
            if (DateTimeOffset.UtcNow < deadline)
                return;
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: inventory did not confirm the purchase.");
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
        // Buyers always pay a 5% market-board fee. Store a conservative landed
        // unit cost so later repricing cannot sell below the actual purchase cost.
        var landedCostPerUnit = (uint)Math.Min(
            PricingStrategyService.MaximumListingPrice,
            decimal.Ceiling(actual.PricePerUnit * 1.05m));
        pricingRule.CostBasis = Math.Max(pricingRule.CostBasis, landedCostPerUnit);
        pricingRule.MinimumMarginPercent = Math.Max(
            pricingRule.MinimumMarginPercent, configuration.Current.ProcurementMinimumRoiPercent);
        configuration.Current.PerItemRules[actual.ItemId] = pricingRule;
        configuration.Save();
        var cost = (ulong)actual.PricePerUnit * actual.Quantity;
        gilSpent += (uint)Math.Min(cost, uint.MaxValue - gilSpent);
        log.Add(AutomationLogLevel.Information,
            $"PURCHASED {actual.ItemName} x{actual.Quantity} on {actual.WorldName} at {actual.PricePerUnit:N0} gil each; " +
            $"tracked landed cost {landedCostPerUnit:N0} gil including buyer fee.");
        AdvanceOrder();
    }

    private void SkipCurrentOrder(string message)
    {
        log.Add(AutomationLogLevel.Warning, message);
        AdvanceOrder();
    }

    private void AdvanceOrder()
    {
        orderIndex++;
        currentOrder = null;
        currentLiveListing = null;
        nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(1_200);
        detail = "Waiting before the next live market request.";
        // Reuse the listings state as a throttled handoff; it will call BeginCurrentOrder.
        State = ProcurementState.WaitingForListings;
    }

    private void ReturnHome()
    {
        market.CloseMarketBoard();
        if (IsOnWorld(homeWorld))
        {
            Delay(ProcurementState.WaitingAfterHomeArrival, "Preparing to return to the summoning bell.", 3_000);
            return;
        }
        var accepted = lifestream.IsAvailable
            ? lifestream.ChangeWorld(homeWorld)
            : commandManager.ProcessCommand($"/li {homeWorld}");
        if (!accepted)
        {
            Halt("Lifestream was busy or did not accept the return-home command.");
            return;
        }
        Wait(ProcurementState.WaitingForHomeWorld, $"Returning to {homeWorld}.", 180);
    }

    private void PollLocalTravel(string objectName, ProcurementState foundState, bool returningHome)
    {
        if (market.FindNearest(objectName).HasValue)
        {
            State = foundState;
            detail = $"Found {objectName}; preparing to approach it.";
            deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            return;
        }

        if (lifestream.IsBusy)
        {
            detail = returningHome
                ? "Lifestream is moving to the home market area."
                : $"Lifestream is moving to {WorldName}'s market area.";
            if (stockHuntScanning && DateTimeOffset.UtcNow >= deadline)
                SkipStockHuntWorld($"LIVE TOUR timed out reaching {WorldName}'s {objectName}; skipping that world.");
            else
                CheckTimeout($"Lifestream did not finish travelling to the market area near {objectName}.");
            return;
        }

        if (DateTimeOffset.UtcNow < nextActionAt)
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

        localTravelAttempts++;
        if (!TryExecuteMarketTravel())
        {
            nextActionAt = DateTimeOffset.UtcNow.AddSeconds(4);
            detail = $"Lifestream did not accept market-area attempt {localTravelAttempts}/3; waiting to retry.";
            log.Add(AutomationLogLevel.Warning, detail);
            return;
        }

        nextActionAt = DateTimeOffset.UtcNow.AddSeconds(12);
        detail = $"Market-area travel attempt {localTravelAttempts}/3 accepted; waiting for {objectName} to load.";
        log.Add(AutomationLogLevel.Information, detail);
    }

    private bool TryExecuteMarketTravel()
    {
        var command = configuration.Current.MarketBoardTravelCommand.Trim();
        if (lifestream.IsAvailable && command.StartsWith("/li", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = command.Length > 3 ? command[3..].Trim() : string.Empty;
            return lifestream.ExecuteCommand(arguments);
        }
        return commandManager.ProcessCommand(command);
    }

    private void FindAndApproach(string objectName, ProcurementState movingState, ProcurementState openedState)
    {
        var position = market.FindNearest(objectName);
        if (!position.HasValue)
        {
            if (stockHuntScanning && DateTimeOffset.UtcNow >= deadline)
                SkipStockHuntWorld($"LIVE TOUR could not find {WorldName}'s {objectName}; skipping that world.");
            else
                CheckTimeout($"Could not find a {objectName} before the timeout.");
            return;
        }
        if (market.DistanceTo(position.Value) <= 4.5f)
        {
            if (market.InteractNearest(objectName))
                Wait(openedState, $"Opening {objectName}.", 15);
            return;
        }
        if (!vnavmesh.IsReady || !vnavmesh.MoveTo(position.Value, 3f))
        {
            if (stockHuntScanning && DateTimeOffset.UtcNow >= deadline)
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
        if (stockHuntScanning && DateTimeOffset.UtcNow >= deadline)
            SkipStockHuntWorld($"LIVE TOUR timed out walking to {WorldName}'s {objectName}; skipping that world.");
        else
            CheckTimeout($"Timed out walking to {objectName}.");
    }

    private void TryAutomaticStart()
    {
        if (!configuration.Current.AutomaticProcurementEnabled || !retainerListings.IsRetainerListOpen ||
            repricing.IsActive)
            return;

        if (configuration.Current.LiveWorldStockHuntEnabled &&
            configuration.Current.AllowAutomaticPurchases &&
            repricing.LastKnownFreeSaleSlots is > 0 &&
            DateTimeOffset.UtcNow >= nextLiveStockHunt && HasLowCuratedStock())
        {
            StartLiveStockHunt();
            return;
        }

        // A completed retainer pass gives us an authoritative free-slot count. Scan
        // immediately when that capacity changes (a listing sold), otherwise use the
        // configured periodic interval while the character remains idle at the bell.
        var freeSaleSlots = repricing.LastKnownFreeSaleSlots;
        var newlyAvailableCapacity = freeSaleSlots is > 0 &&
                                     Math.Min(freeSaleSlots.Value, configuration.Current.ProcurementTargetSaleSlots) !=
                                     lastScannedFreeSaleSlots;
        if (!newlyAvailableCapacity && DateTimeOffset.UtcNow < nextAutomaticScan)
            return;
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        StartScan(ProcurementRunMode.AutomaticPurchase);
    }

    private bool HasLowCuratedStock() => configuration.Current.ProcurementRules
        .Where(x => x.Enabled && x.ItemId != 0 && x.RequireHighQuality)
        .Any(x => market.GetInventoryCount(x.ItemId, true) < configuration.Current.LiveWorldStockThresholdPerItem);

    private bool IsOnWorld(string world) => playerState.IsLoaded && string.Equals(
        playerState.CurrentWorld.Value.Name.ToString(), world, StringComparison.OrdinalIgnoreCase);

    private void Complete(string message)
    {
        activeRunMode = ProcurementRunMode.None;
        taskbarAttention.StopFlashing();
        State = ProcurementState.Completed;
        detail = message;
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        log.Add(AutomationLogLevel.Information, message);
    }

    private void Wait(ProcurementState state, string message, int seconds)
    {
        State = state;
        detail = message;
        deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private void Delay(ProcurementState state, string message, int milliseconds)
    {
        State = state;
        detail = message;
        nextActionAt = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
    }

    private void CheckTimeout(string message)
    {
        if (DateTimeOffset.UtcNow >= deadline)
            Halt(message);
    }

    private bool DelayElapsed() => DateTimeOffset.UtcNow >= nextActionAt;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        cancellation?.Cancel();
        cancellation?.Dispose();
        vnavmesh.Stop();
        taskbarAttention.StopFlashing();
    }
}
