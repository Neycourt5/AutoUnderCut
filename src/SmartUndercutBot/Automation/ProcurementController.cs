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

public sealed partial class ProcurementController : IDisposable
{
    private static readonly string[] NorthAmericaWorlds =
    [
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
        "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph",
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
    private readonly IRetainerAutomation repricing;
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
    private DateTimeOffset resumeStoppedRouteAt = DateTimeOffset.MaxValue;
    private int worldIndex;
    private int orderIndex;
    private int inventoryBefore;
    private int lastScannedFreeSaleSlots = -1;
    private uint lastScannedBudget;
    private ProcurementRunMode runAfterScan;
    private ProcurementRunMode activeRunMode;
    private string homeWorld = string.Empty;
    private string planningHomeWorld = string.Empty;
    private bool retryReturnHome;
    private bool ownsRetainerPause;
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
    private int purchaseConfirmations;
    private int skippedPurchases;
    private string routeOutcome = string.Empty;
    private int consecutiveFailedWorlds;
    private int worldSuccessfulScans;
    private readonly Dictionary<uint, int> purchasedSlotsByItem = [];
    private IReadOnlyList<PortfolioDecision> portfolioDecisions = [];
    private readonly MarketDiscoveryService? discovery;

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
        IRetainerAutomation repricing,
        ConfigurationService configuration,
        AutomationLog log,
        TimeProvider? timeProvider = null,
        MarketDiscoveryService? discovery = null)
    {
        this.discovery = discovery;
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
    public bool RequiresManualRestart => (State is ProcurementState.Halted or ProcurementState.Faulted) &&
        resumeStoppedRouteAt == DateTimeOffset.MaxValue;
    public bool IsWaitingToReturnHome => retryReturnHome && !RequiresManualRestart;
    public int ResaleBagSlots => CollectBagStock().Sum(x => x.SaleSlots);
    public int MarketableBagSlots => retainerListings.ReadBagListingCandidates().Count;
    public int ComfortableStockTarget => ResaleStockPolicy.ComfortableBagTarget(
        (repricing.LastKnownFreeSaleSlots ?? 0) + repricing.ListedStock.Sum(x => x.SaleSlots));
    public int PurchaseCapacity => AvailablePurchaseSlots();
    public uint ShoppingBudget => SpendableGil(newTrip: true);
    public string? ShoppingWaitReason => repricing.LastKnownFreeSaleSlots is null
        ? "waiting for a complete retainer check"
        : market.FreeInventorySlots <= configuration.Current.ProcurementInventoryReserve ? "waiting for free bag space"
        : AvailablePurchaseSlots() == 0 ? configuration.Current.ContinueShoppingWhenStocked
            ? $"comfortable trading stock is ready ({ResaleBagSlots}/{ComfortableStockTarget} sale stacks); watching for restocks"
            : "the fixed spare-stock target is reached"
        : ShoppingBudget == 0 ? "waiting for sale income or room in the buffer budget" : null;
    public string? TravelReadinessIssue => !lifestream.IsAvailable
        ? "Enable Lifestream to travel and return home."
        : lifestream.IsBusy ? "Wait for the current Lifestream journey to finish."
        : !vnavmesh.IsReady ? "Enable vnavmesh and wait for its navigation mesh to load." : null;

    public void ResumeAutomatic()
    {
        if (IsActive)
            return;
        resumeStoppedRouteAt = DateTimeOffset.MaxValue;
        retryReturnHome = false;
        ownsRetainerPause = false;
        nextAutomaticScan = timeProvider.GetUtcNow();
        lastScannedFreeSaleSlots = -1;
        lastScannedBudget = 0;
        Plan = ProcurementPlan.Empty;
        State = ProcurementState.Idle;
        detail = "Waiting for the retainer check and bag refill before shopping.";
    }
    public bool IsActive => State is not (ProcurementState.Idle or ProcurementState.PlanReady or ProcurementState.Completed or ProcurementState.Halted or ProcurementState.Faulted);
    public bool IsGuidedReviewPending => State == ProcurementState.AwaitingManualReview;
    public string CurrentGuidedWorld => IsGuidedReviewPending ? WorldName : string.Empty;
    public int CurrentGuidedWorldNumber => IsGuidedReviewPending ? worldIndex + 1 : 0;
    public int GuidedWorldCount => worldGroups.Count;
    public IReadOnlyList<ProcurementOrder> CurrentGuidedWorldOrders =>
        IsGuidedReviewPending && worldIndex < worldGroups.Count ? worldGroups[worldIndex].ToArray() : [];
    public ProcurementStatus Status => new(
        State, detail, confirmedPurchases, Plan.Orders.Count, gilSpent,
        configuration.Current.AutomaticProcurementEnabled && !RequiresManualRestart
            ? State is ProcurementState.Halted or ProcurementState.Faulted ? resumeStoppedRouteAt
                : configuration.Current.LiveWorldStockHuntEnabled ? nextLiveStockHunt : nextAutomaticScan : null);

    public void ScanNow() => StartScan(ProcurementRunMode.None);

    public void RunNow()
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        if (configuration.Current.PriorityShoppingEnabled)
        {
            StartScan(ProcurementRunMode.AutomaticPurchase);
            return;
        }
        if (State != ProcurementState.PlanReady || Plan.Orders.Count == 0 ||
            timeProvider.GetUtcNow() - Plan.CreatedAt > TimeSpan.FromMinutes(5))
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
        => StopRoute(reason, DateTimeOffset.MaxValue);

    // A stop that only reflects a passing condition - no capacity yet, Lifestream
    // still busy, a scan that failed - must not disable unattended procurement for
    // the rest of the session. Those schedule a retry; a user stop and an
    // unverified purchase stay latched until the user looks at them.
    private void HaltForRetry(string reason) => StopRoute(
        reason,
        timeProvider.GetUtcNow().AddMinutes(Math.Max(1, configuration.Current.ProcurementIntervalMinutes)));

    private void StopRoute(string reason, DateTimeOffset resumeAt)
    {
        if (resumeAt == DateTimeOffset.MaxValue)
            ownsRetainerPause = false;
        retryReturnHome = resumeAt != DateTimeOffset.MaxValue && activeRunMode != ProcurementRunMode.None &&
            !string.IsNullOrWhiteSpace(homeWorld) && !retainerListings.IsRetainerListOpen;
        cancellation?.Cancel();
        scanTask = null;
        vnavmesh.Stop();
        if (activeRunMode != ProcurementRunMode.None)
            lifestream.Abort();
        taskbarAttention.StopFlashing();
        stockHuntScanning = false;
        activeRunMode = ProcurementRunMode.None;
        resumeStoppedRouteAt = resumeAt;
        State = ProcurementState.Halted;
        detail = reason;
        log.Add(AutomationLogLevel.Warning, reason);
    }

    private void StartScan(ProcurementRunMode mode)
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        stockHuntScanning = false;
        priorityShopping = mode == ProcurementRunMode.AutomaticPurchase && configuration.Current.PriorityShoppingEnabled;
        // Optional, cached, and never blocking: discovery may add candidates for
        // the next scan, but a failure leaves the curated rules to do their job.
        discovery?.ApplyPendingDiscoveries();
        discovery?.RefreshIfDue();
        var dataCenter = ProcurementTravelPolicy.ShoppingScope(
            universalis.ResolveDataCenter(configuration.Current.ProcurementDataCenter));
        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            HaltForRetry("Could not determine a Universalis data center. Set it in the Procurement tab.");
            return;
        }
        // Sell-only stock is never bought, so asking Universalis about it only makes
        // the request enormous. Hundreds of seeded dye and materia rules are what
        // turned this scan into a 504.
        var buyRules = configuration.Current.ProcurementRules
            .Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .DistinctBy(x => x.ItemId)
            .ToArray();
        if (buyRules.Length == 0)
        {
            HaltForRetry("No buyable procurement items are configured. Add the favorite defaults in the Procurement tab.");
            return;
        }
        if (!TryGetHomeWorld(out planningHomeWorld))
        {
            HaltForRetry("Wait for the character's home world to load before searching for resale stock.");
            return;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        runAfterScan = mode;
        scanTask = priorityShopping
            ? ScanPriorityRegionAsync(buyRules, planningHomeWorld, cancellation.Token)
            : ScanWithHomeResaleAsync(buyRules, dataCenter, planningHomeWorld, cancellation.Token);
        // Room for three attempts per batch with backoff before giving up.
        deadline = timeProvider.GetUtcNow().AddSeconds(150);
        Plan = ProcurementPlan.Empty;
        confirmedPurchases = 0;
        purchaseConfirmations = 0;
        skippedPurchases = 0;
        gilSpent = 0;
        State = ProcurementState.ScanningUniversalis;
        detail = priorityShopping
            ? $"Comparing {buyRules.Length} flips across North America against home sales to shortlist quick visits."
            : $"Scanning {buyRules.Length} item(s) on {dataCenter} via Universalis.";
        log.Add(AutomationLogLevel.Information, detail);
    }

    private async Task<IReadOnlyList<ProcurementMarketItem>> ScanWithHomeResaleAsync(
        IReadOnlyList<ProcurementRule> rules, string scope, string resaleWorld, CancellationToken token)
    {
        var regional = universalis.ScanAsync(rules, scope, token);
        var local = string.Equals(scope, resaleWorld, StringComparison.OrdinalIgnoreCase)
            ? regional : universalis.ScanAsync(rules, resaleWorld, token);
        await Task.WhenAll(regional, local).ConfigureAwait(false);
        var homeMarkets = local.Result.ToDictionary(x => x.ItemId);
        return regional.Result.Select(market =>
        {
            homeMarkets.TryGetValue(market.ItemId, out var home);
            return market with
            {
                RecentSales = home?.RecentSales ?? [],
                NqSalesPerDay = home?.NqSalesPerDay,
                HqSalesPerDay = home?.HqSalesPerDay,
                Listings = market.Listings
                    .Where(x => !string.Equals(x.WorldName, resaleWorld, StringComparison.OrdinalIgnoreCase))
                    .Concat(home?.Listings ?? []).Distinct().ToArray(),
            };
        }).ToArray();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            // Never restart buying after an exception while a submitted purchase
            // is awaiting confirmation. Its outcome must be checked by the user.
            if (State == ProcurementState.WaitingForPurchase)
            {
                Halt($"PURCHASE OUTCOME UNKNOWN: {ex.Message}. Check inventory before restarting.");
                return;
            }
            retryReturnHome = activeRunMode != ProcurementRunMode.None &&
                !string.IsNullOrWhiteSpace(homeWorld) && !retainerListings.IsRetainerListOpen;
            cancellation?.Cancel();
            scanTask = null;
            if (activeRunMode != ProcurementRunMode.None)
                lifestream.Abort();
            activeRunMode = ProcurementRunMode.None;
            stockHuntScanning = false;
            resumeStoppedRouteAt = timeProvider.GetUtcNow()
                .AddMinutes(Math.Max(1, configuration.Current.ProcurementIntervalMinutes));
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
        {
            TryResumeAfterStop();
            return;
        }

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
                        FinishShopping("The Market Board closed before its item-search services became ready.");
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
        {
            CheckTimeout("Universalis did not answer within 150 seconds; the scan was cancelled and will retry.");
            return;
        }
        if (scanTask.IsCanceled || scanTask.IsFaulted)
        {
            HaltForRetry(scanTask.Exception?.GetBaseException().Message ?? "Universalis scan was cancelled.");
            return;
        }
        var config = configuration.Current;
        if (priorityShopping)
        {
            var markets = scanTask.Result;
            scanTask = null;
            lastScannedFreeSaleSlots = AvailablePurchaseSlots();
            lastScannedBudget = ShoppingBudget;
            nextAutomaticScan = timeProvider.GetUtcNow().AddMinutes(config.ProcurementIntervalMinutes);
            State = ProcurementState.PlanReady;
            BeginPriorityShopping(markets);
            return;
        }
        var hasHomeResaleData = scanTask.Result.Any(x => x.RecentSales.Count > 0 &&
            x.Listings.Any(y => string.Equals(y.WorldName, planningHomeWorld, StringComparison.OrdinalIgnoreCase) &&
                                y.PricePerUnit > 0 && y.Quantity > 0 &&
                                !retainerListings.OwnedRetainerIds.Contains(y.RetainerId)));
        // These markets already carry home-world sales history and velocity, so the
        // portfolio can tier owned stock on real demand rather than assuming the
        // worst about everything that is not pinned.
        priorityDemand = scanTask.Result;
        var freeSaleSlots = repricing.LastKnownFreeSaleSlots ?? config.ProcurementTargetSaleSlots;
        var plannedSaleSlots = PlannedSaleSlots(freeSaleSlots);
        var freeInventorySlots = Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve);
        Plan = planner.BuildPlan(new(
            ShoppingMarkets(scanTask.Result),
            ShoppingRules(config.ProcurementRules),
            SpendableGil(),
            plannedSaleSlots,
            freeInventorySlots,
            config.ProcurementMinimumRoiPercent,
            config.ProcurementMinimumProfitPerUnit,
            HomeWorld: planningHomeWorld,
            OwnedRetainerIds: retainerListings.OwnedRetainerIds,
            OwnedStock: CollectOwnedStock(),
            MaximumWeeklySalesSharePercent: config.ProcurementWeeklySalesSharePercent,
            HighQualityOnly: config.BuyHighQualityOnly,
            Portfolio: config.PortfolioGates,
            PortfolioCapacitySlots: PortfolioCapacitySlots()));
        LogPortfolioDecisions("DEAL SEARCH", Plan);
        lastScannedFreeSaleSlots = plannedSaleSlots;
        lastScannedBudget = ShoppingBudget;
        scanTask = null;
        nextAutomaticScan = timeProvider.GetUtcNow().AddMinutes(config.ProcurementIntervalMinutes);
        State = ProcurementState.PlanReady;
        detail = Plan.Orders.Count == 0
            ? !hasHomeResaleData
                ? $"No usable sale history and competing prices were found for {planningHomeWorld}. Waiting for home-world resale data before buying."
                : ShoppingWaitReason is { } reason
                    ? $"Deal search finished; buying is {reason}. Searches and retainer checks will repeat."
                    : "No deals passed the volume, margin, budget, bag-slot, and sale-slot guards. The next search will retry."
            : $"Plan ready: {Plan.Orders.Count} stack(s), {Plan.TotalCost:N0} gil, about {Plan.ExpectedProfit:N0} gil expected profit.";
        log.Add(AutomationLogLevel.Information, detail);
        if (runAfterScan != ProcurementRunMode.None && Plan.Orders.Count > 0)
            BeginExecution(runAfterScan);
    }

    private void StartLiveStockHunt()
    {
        if (IsActive || IsStartBlocked?.Invoke() == true)
            return;
        priorityShopping = false;
        if (!configuration.Current.AllowAutomaticPurchases)
        {
            State = ProcurementState.Halted;
            detail = "Arm automatic market-board purchases before starting the live all-world stock hunt.";
            log.Add(AutomationLogLevel.Warning, detail);
            return;
        }
        if (!playerState.IsLoaded)
        {
            HaltForRetry("The character is not fully loaded.");
            return;
        }
        if (!lifestream.IsAvailable || lifestream.IsBusy)
        {
            HaltForRetry("Lifestream must be enabled and idle before starting a live tour.");
            return;
        }
        if (repricing.LastKnownFreeSaleSlots is null)
        {
            HaltForRetry("The live all-world stock hunt needs a completed all-retainer bell pass first.");
            return;
        }
        if (AvailablePurchaseSlots() == 0 || market.FreeInventorySlots <= configuration.Current.ProcurementInventoryReserve || ShoppingBudget == 0)
        {
            HaltForRetry("No shopping capacity: list pending stock first and keep bag space and gil available.");
            return;
        }

        if (!TryGetHomeWorld(out homeWorld))
        {
            HaltForRetry("Could not determine the character's home world.");
            return;
        }
        stockHuntRules = ResaleStockPolicy.SelectTourRules(
            configuration.Current.ProcurementRules, IsBelowStockThreshold,
            configuration.Current.LiveWorldStockHuntMaximumItems).ToList();
        if (stockHuntRules.Count == 0)
        {
            State = ProcurementState.Completed;
            detail = $"No configured item is below the live-tour threshold of {configuration.Current.LiveWorldStockThresholdPerItem:N0}.";
            log.Add(AutomationLogLevel.Information, detail);
            return;
        }

        ledger.ClearBagStockQueue();
        repricing.Halt("Paused while the live all-world stock hunt runs.");
        ownsRetainerPause = true;
        market.CloseRetainerList();
        activeRunMode = ProcurementRunMode.AutomaticPurchase;
        runAfterScan = ProcurementRunMode.None;
        Plan = ProcurementPlan.Empty;
        gilSpent = 0;
        confirmedPurchases = 0;
        purchaseConfirmations = 0;
        skippedPurchases = 0;
        successfulLiveScans = 0;
        failedLiveScans = 0;
        routeOutcome = string.Empty;
        consecutiveFailedWorlds = 0;
        worldSuccessfulScans = 0;
        purchasedSlotsByItem.Clear();
        stockHuntListings.Clear();
        // Scan the home world last. Its prices are the resale anchor for every
        // prospective deal, so they should be the freshest data in the tour
        // when the guarded purchase plan is built.
        stockHuntWorlds = NorthAmericaWorlds
            .Where(x => !string.Equals(x, homeWorld, StringComparison.OrdinalIgnoreCase))
            .Append(homeWorld)
            .Where(ProcurementTravelPolicy.CanShopOnWorld)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        stockHuntWorldIndex = 0;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        stockHuntScanning = true;
        nextLiveStockHunt = timeProvider.GetUtcNow().AddMinutes(configuration.Current.LiveWorldStockHuntCooldownMinutes);
        detail = $"Starting live stock hunt for {stockHuntRules.Count} low-stock item(s) (maximum 8) across {stockHuntWorlds.Count} North American worlds.";
        log.Add(AutomationLogLevel.Information, detail);
        TravelToCurrentWorld();
    }

    private void BeginExecution(ProcurementRunMode mode, bool preserveTrip = false)
    {
        // The live tour reaches this after its scan, so returning silently would
        // leave the route dangling mid-state. Stop explicitly and retry later.
        if (IsStartBlocked?.Invoke() == true)
        {
            HaltForRetry("Another automation pass started first; the procurement route was not begun.");
            return;
        }
        if (mode == ProcurementRunMode.AutomaticPurchase && !configuration.Current.AllowAutomaticPurchases)
        {
            State = ProcurementState.PlanReady;
            detail = "Purchase writes are disarmed; review the plan and arm them before running.";
            return;
        }
        if (mode == ProcurementRunMode.AutomaticPurchase &&
            (AvailablePurchaseSlots() == 0 || market.FreeInventorySlots <= configuration.Current.ProcurementInventoryReserve))
        {
            HaltForRetry("The plan no longer has free retainer or inventory capacity. List pending stock before buying more.");
            return;
        }
        if (Plan.Orders.Count == 0 || ShoppingBudget < Plan.TotalCost)
        {
            HaltForRetry("The procurement plan is empty or no longer fits the available gil balance.");
            return;
        }
        if (!TryGetHomeWorld(out homeWorld))
        {
            HaltForRetry("Could not determine the character's home world.");
            return;
        }
        if (!lifestream.IsAvailable || lifestream.IsBusy)
        {
            HaltForRetry("Lifestream must be enabled and idle before starting a route.");
            return;
        }

        // Purchased stock owns its landed-cost ledger entries. Drop an older bag-refill
        // queue first so the two sources cannot merge under the same item key.
        ledger.ClearBagStockQueue();
        repricing.Halt("Paused while procurement runs.");
        ownsRetainerPause = true;
        activeRunMode = mode;
        runAfterScan = ProcurementRunMode.None;
        market.CloseRetainerList();
        var orderedWorlds = Plan.Orders.Where(x => ProcurementTravelPolicy.CanShopOnWorld(x.WorldName))
            .GroupBy(x => x.WorldName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Sum(y => (long)y.ExpectedProfit));
        worldGroups = (mode == ProcurementRunMode.GuidedReview
                ? orderedWorlds.Take(configuration.Current.GuidedTourMaximumWorlds)
                : orderedWorlds)
            .ToList();
        worldIndex = 0;
        orderIndex = 0;
        if (!preserveTrip)
        {
            gilSpent = 0;
            confirmedPurchases = 0;
            purchaseConfirmations = 0;
            skippedPurchases = 0;
            purchasedSlotsByItem.Clear();
        }
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
            HaltForRetry("Lifestream is unavailable; the route cannot continue.");
            return;
        }
        var destination = returningHome ? homeWorld : WorldName;
        if (!returningHome && !ProcurementTravelPolicy.CanShopOnWorld(destination))
        {
            FinishShopping($"Shopping on {destination} is excluded; returning home.");
            return;
        }
        // The public Lifestream IPC can acknowledge a world change without
        // beginning travel on some versions. The literal chat command is the
        // same path the user has confirmed works reliably.
        var currentWorld = playerState.CurrentWorld.Value.Name.ToString();
        var sourceIndex = Array.FindIndex(NorthAmericaWorlds, x => x.Equals(currentWorld, StringComparison.OrdinalIgnoreCase));
        var targetIndex = Array.FindIndex(NorthAmericaWorlds, x => x.Equals(destination, StringComparison.OrdinalIgnoreCase));
        var viaLimsa = sourceIndex >= 0 && targetIndex >= 0 &&
            lifestream.TryChangeWorldViaLimsa(destination, sourceIndex / 8 != targetIndex / 8);
        var accepted = viaLimsa || commandManager.ProcessCommand($"/li {destination}");
        if (!accepted)
        {
            if (stockHuntScanning)
            {
                SkipStockHuntWorld($"Lifestream could not visit {WorldName}; skipping that world.");
                return;
            }
            FailDestination("The /li world-travel command was not accepted.");
            return;
        }
        Wait(returningHome ? ProcurementState.WaitingForHomeWorld : ProcurementState.WaitingForWorld,
            $"Travelling to {destination} with Lifestream{(viaLimsa ? " via Limsa" : string.Empty)}.", 600);
    }

    private void BeginLocalTravel(bool returningHome)
    {
        var objectName = returningHome ? "Summoning Bell" : "Market Board";
        // A loaded nearby object can be walked to without paying for a teleport.
        if (!lifestream.IsBusy && market.FindNearest(objectName) is { } nearby && market.DistanceTo(nearby) <= 120f)
        {
            nextActionAt = timeProvider.GetUtcNow();
            Wait(returningHome ? ProcurementState.FindingSummoningBell : ProcurementState.FindingMarketBoard,
                $"Walking to the nearby {objectName}; no local teleport needed.", 60);
            return;
        }
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
        while (priorityShopping && stockHuntWorldIndex > 0 && stockHuntRuleIndex < stockHuntRules.Count &&
            !ShouldScoutItem(stockHuntRules[stockHuntRuleIndex].ItemId)) stockHuntRuleIndex++;
        if (stockHuntRuleIndex >= stockHuntRules.Count)
        {
            FinishStockHuntWorld();
            return;
        }
        if (PriorityTripShouldReturn()) return;

        // On the home world, an item whose price was already read - by an earlier
        // sweep or, far more often, by the retainer pass repricing it - does not
        // need reading again. That is what made the home leg take so long.
        if (priorityShopping && stockHuntWorldIndex == 0 &&
            HomePriceIsFresh(stockHuntRules[stockHuntRuleIndex].ItemId))
        {
            worldSuccessfulScans++; // Reused evidence is not a failed server read.
            stockHuntRuleIndex++;
            nextActionAt = timeProvider.GetUtcNow();
            State = ProcurementState.WaitingForStockHuntListings;
            detail = "Reusing a home price already read this cycle.";
            return;
        }

        currentStockHuntRule = stockHuntRules[stockHuntRuleIndex];
        listingRequestAttempts = 1;
        if (!market.RequestListings(currentStockHuntRule.ItemId))
        {
            nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            Wait(ProcurementState.WaitingForStockHuntListings,
                $"Waiting to scan {currentStockHuntRule.ItemName} on {WorldName}.", 6);
            return;
        }
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        Wait(ProcurementState.WaitingForStockHuntListings,
            $"Reading live {currentStockHuntRule.ItemName} listings on {WorldName}.", 6);
    }

    private void PollStockHuntListings()
    {
        if (!IsOnWorld(WorldName) || !market.IsMarketBoardOpen || lifestream.IsBusy)
        {
            SkipStockHuntWorld("Live scan lost its destination world or Market Board; discarding this world's data.");
            return;
        }
        if (currentStockHuntRule is null)
        {
            if (DelayElapsed())
                BeginStockHuntRuleScan();
            return;
        }

        if (!market.AreListingsReady(currentStockHuntRule.ItemId))
        {
            if (market.SearchStatus is { } searchStatus) detail = searchStatus;
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
            .Where(x => ResaleStockPolicy.BuyableQuality(
                currentStockHuntRule, x.IsHighQuality, configuration.Current.BuyHighQualityOnly))
            .ToArray();
        successfulLiveScans++;
        worldSuccessfulScans++;
        if (priorityShopping)
        {
            ObservePriorityItem(live);
            return;
        }
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
            $"LIVE TOUR {WorldName}: {currentStockHuntRule.ItemName} returned {live.Length} eligible listing(s).");
        AdvanceStockHuntRule();
    }

    private void AdvanceStockHuntRule()
    {
        market.ResetListingRequest();
        stockHuntRuleIndex++;
        currentStockHuntRule = null;
        nextActionAt = timeProvider.GetUtcNow();
        State = ProcurementState.WaitingForStockHuntListings;
        detail = $"Waiting before the next live scan on {WorldName}.";
    }

    private void FinishStockHuntWorld()
    {
        var completedWorld = WorldName;
        if (priorityShopping && stockHuntWorldIndex == 0 && homePrices.Values.All(x => x.Length == 0))
        {
            FinishShopping("No confirmed home prices arrived. Retainer checks continue; shopping will retry.");
            return;
        }
        consecutiveFailedWorlds = worldSuccessfulScans == 0 ? consecutiveFailedWorlds + 1 : 0;
        worldSuccessfulScans = 0;
        if (StopUnproductiveTour())
            return;
        market.CloseMarketBoard();
        stockHuntWorldIndex++;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        if (priorityShopping)
        {
            if (stockHuntWorldIndex == 1)
            {
                priorityDepartedAt = timeProvider.GetUtcNow();
                var resumeItem = stockHuntRules.FindIndex(x => x.ItemId == configuration.Current.PriorityNextItem);
                if (resumeItem >= 0) stockHuntRuleIndex = resumeItem;
            }
            else priorityWorldsCompleted++;
            if (stockHuntWorldIndex >= stockHuntWorlds.Count)
            {
                CompleteStockHuntScan();
                return;
            }
            SavePriorityCursor();
            if (PriorityTripShouldReturn()) return;
        }
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
        stockHuntListings.RemoveAll(x => string.Equals(x.WorldName, WorldName, StringComparison.OrdinalIgnoreCase));
        consecutiveFailedWorlds++;
        worldSuccessfulScans = 0;
        if (StopUnproductiveTour())
            return;
        stockHuntWorldIndex++;
        stockHuntRuleIndex = 0;
        currentStockHuntRule = null;
        SavePriorityCursor();
        TravelToCurrentWorld();
    }

    private void CompleteStockHuntScan()
    {
        if (priorityShopping)
        {
            configuration.Current.PriorityNextWorld = string.Empty;
            configuration.Current.PriorityNextItem = 0;
            configuration.Current.PriorityScoutRoute.Clear();
            configuration.Save();
            FinishPriorityScouting("Priority circuit compared. The next circuit starts at Aether.");
            return;
        }
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
            ShoppingMarkets(markets),
            ShoppingRules(stockHuntRules),
            homeWorld,
            retainerListings.OwnedRetainerIds,
            SpendableGil(),
            PlannedSaleSlots(freeSaleSlots),
            Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve),
            config.ProcurementMinimumRoiPercent,
            config.ProcurementMinimumProfitPerUnit,
            OwnedStock: CollectOwnedStock(),
            HighQualityOnly: config.BuyHighQualityOnly,
            Portfolio: config.PortfolioGates,
            PortfolioCapacitySlots: PortfolioCapacitySlots()));
        LogPortfolioDecisions("LIVE TOUR", Plan);
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
                    ? $"No purchases: no usable resale prices were received from {homeWorld}. The purchase plan could not be built."
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
        if (AvailablePurchaseSlots() == 0)
        {
            FinishShopping("Shopping stopped: all available retainer slots are reserved for purchased stock.");
            return;
        }
        listingRequestAttempts = 1;
        nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
        if (!market.RequestListings(currentOrder.ItemId))
        {
            nextActionAt = timeProvider.GetUtcNow().AddMilliseconds(1_200);
            Wait(ProcurementState.WaitingForListings, $"Waiting to request {currentOrder.ItemName}.", 6);
            return;
        }
        Wait(ProcurementState.WaitingForListings, $"Revalidating live prices for {currentOrder.ItemName}.", 6);
    }

    private void PollListings()
    {
        if (priorityShopping && !DelayElapsed()) return;
        if (priorityShopping && !stockHuntScanning && comparisonBuyingStarted is { } started &&
            timeProvider.GetUtcNow() - started >= TimeSpan.FromMinutes(20))
        {
            FinishShopping("Compared buying pass reached its time limit; returning to list stock and collect sales.");
            return;
        }
        if (priorityShopping && currentOrder is { } checkedOrder && !HomePriceIsFresh(checkedOrder.ItemId))
        {
            SkipCurrentOrder($"SKIPPED BUY {checkedOrder.ItemName}: home reference expired; refresh it next retainer pass.");
            return;
        }
        if (!configuration.Current.AllowAutomaticPurchases)
        {
            FinishShopping("Automatic purchases were disarmed; the route stopped before submitting another purchase.");
            return;
        }
        if (!IsOnWorld(WorldName) || !market.IsMarketBoardOpen || lifestream.IsBusy)
        {
            FinishShopping("The destination world or Market Board changed before purchase validation.");
            return;
        }
        if (currentOrder is null)
        {
            if (!DelayElapsed())
                return;
            BeginCurrentOrder();
            return;
        }
        if (currentOrder.IsFillOrder && ResaleBagSlots >= Math.Min(repricing.LastKnownFreeSaleSlots ?? 0,
            configuration.Current.ProcurementTargetSaleSlots))
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: empty retainer slots are already covered; the lower fill margin no longer applies.");
            return;
        }
        // The lower fill margin exists to keep good stock flowing, not to occupy a
        // slot. Opportunistic stock never reaches it, even through a stale plan.
        if (currentOrder.IsFillOrder && currentOrder.Tier == PortfolioTier.Opportunistic)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: opportunistic stock cannot use the lower fill margin.");
            return;
        }
        // An opportunistic buy planned before other stock was listed must not push
        // the portfolio past its cap.
        if (currentOrder.Tier == PortfolioTier.Opportunistic && PortfolioSummary is { OpportunisticHeadroom: <= 0 } portfolio)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: {portfolio}. The opportunistic cap is reached.");
            return;
        }
        var rule = ShoppingRules(configuration.Current.ProcurementRules).FirstOrDefault(x => x.ItemId == currentOrder.ItemId);
        if (rule is null || !rule.Enabled || rule.LiquidateOnly ||
            !ResaleStockPolicy.BuyableQuality(rule, currentOrder.IsHighQuality, configuration.Current.BuyHighQualityOnly) ||
            CollectOwnedStock().Where(x => x.ItemId == currentOrder.ItemId).Sum(x => x.SaleSlots) >= rule.MaximumSaleSlots)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: its current rule no longer permits this order.");
            return;
        }
        currentOrder = currentOrder with
        {
            MaximumAcceptableUnitPrice = rule.MaximumUnitPrice > 0
                ? Math.Min(currentOrder.MaximumAcceptableUnitPrice, rule.MaximumUnitPrice)
                : currentOrder.MaximumAcceptableUnitPrice,
            Quantity = Math.Min(currentOrder.Quantity, (uint)Math.Max(1, rule.TargetStackSize)),
        };
        if (!market.AreListingsReady(currentOrder.ItemId))
        {
            if (market.SearchStatus is { } searchStatus) detail = searchStatus;
            if (timeProvider.GetUtcNow() >= deadline)
            {
                if (RetryListingRequest(currentOrder.ItemName))
                    return;
                market.ResetListingRequest();
                if (priorityShopping)
                {
                    scoutObservedAt.Remove((WorldName, currentOrder.ItemId));
                    scoutListings.RemoveAll(x => x.WorldName == WorldName && x.ItemId == currentOrder.ItemId);
                }
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
        if (priorityShopping && !stockHuntScanning)
        {
            // Refresh remembered offers on a buying revisit too. A sold or repriced
            // winner must not keep winning the next trip from yesterday's cache.
            scoutListings.RemoveAll(x => x.WorldName == WorldName && x.ItemId == currentOrder.ItemId);
            scoutListings.AddRange(market.ReadLiveListings(currentOrder.ItemId).Select(x => new ProcurementMarketListing(
                x.ItemId, x.ListingId, x.RetainerId, WorldName, 0, x.PricePerUnit, x.Quantity, x.IsHighQuality)));
            scoutObservedAt[(WorldName, currentOrder.ItemId)] = timeProvider.GetUtcNow();
        }
        if (!market.TrySelectLiveListing(currentOrder, retainerListings.OwnedRetainerIds, out var live) || live is null)
        {
            // This is the most common skip, so say what the board actually held
            // rather than only that nothing qualified.
            var seen = market.ReadLiveListings(currentOrder.ItemId)
                .Where(x => x.IsHighQuality == currentOrder.IsHighQuality)
                .OrderBy(x => x.PricePerUnit)
                .ToArray();
            var cheapest = seen.FirstOrDefault();
            SkipCurrentOrder(
                $"SKIPPED BUY {currentOrder.ItemName}: no live listing matched the plan. " +
                $"Wanted up to {currentOrder.MaximumAcceptableUnitPrice:N0} gil each for at most " +
                $"{currentOrder.Quantity} unit(s); the board showed {seen.Length} matching-quality listing(s)" +
                (cheapest is null
                    ? "."
                    : $", cheapest {cheapest.PricePerUnit:N0} gil x{cheapest.Quantity}."));
            return;
        }

        var totalCost = GetPurchaseCost(live);
        // Validate the returned candidate independently of the UI adapter.
        if (live.ItemId != currentOrder.ItemId || live.IsHighQuality != currentOrder.IsHighQuality ||
            live.Quantity == 0 || live.Quantity > currentOrder.Quantity || live.PricePerUnit == 0 ||
            live.PricePerUnit > currentOrder.MaximumAcceptableUnitPrice || retainerListings.OwnedRetainerIds.Contains(live.RetainerId))
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the selected live listing failed validation.");
            return;
        }
        var expectedNetProceeds = decimal.Floor(currentOrder.TargetSalePrice * 0.95m) * live.Quantity;
        if (expectedNetProceeds < totalCost * (1m + RequiredPurchaseRoi(currentOrder) / 100m) ||
            expectedNetProceeds - totalCost < (decimal)configuration.Current.ProcurementMinimumProfitPerUnit * live.Quantity)
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the live buyer tax no longer meets the profit guards.");
            return;
        }
        if (AvailablePurchaseSlots() == 0 || market.FreeInventorySlots <= configuration.Current.ProcurementInventoryReserve)
        {
            FinishShopping("Resale capacity or the inventory reserve was reached; returning home to list stock.");
            return;
        }
        if (totalCost > SpendableGil() || totalCost > market.Gil)
        {
            // Out of money mid-route: no later world is affordable either, and the
            // only way to get more gil is the retainer pass at the home bell. Go
            // home and collect sale proceeds instead of touring broke.
            if (SpendableGil() == 0)
            {
                FinishShopping(
                    "Out of spendable gil. Returning home to collect retainer sales before shopping again.");
                return;
            }
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: budget or gil balance changed.");
            return;
        }
        inventoryBefore = market.GetInventoryCount(live.ItemId, live.IsHighQuality);
        currentLiveListing = live;
        // Once submission begins, an exception cannot prove that the game did
        // not receive the request. Enter verification before crossing that boundary.
        purchaseConfirmations = 0;
        Wait(ProcurementState.WaitingForPurchase, $"Waiting for {currentOrder.ItemName} to arrive in inventory.", 30);
        if (!market.SubmitPurchase(live))
        {
            SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: the live purchase request was rejected.");
            return;
        }
    }

    private void PollPurchase()
    {
        if (currentOrder is null || currentLiveListing is null)
        {
            Halt("PURCHASE OUTCOME UNKNOWN: purchase state was lost. Check inventory before restarting.");
            return;
        }
        var count = market.GetInventoryCount(currentLiveListing.ItemId, currentLiveListing.IsHighQuality);
        if (count < inventoryBefore + currentLiveListing.Quantity)
        {
            if (market.PurchaseError is > 0 and var error)
            {
                SkipCurrentOrder($"SKIPPED BUY {currentOrder.ItemName}: server rejected the purchase (error {error}).");
                return;
            }
            if (purchaseConfirmations == 0 && market.TryConfirmPurchase(currentOrder.ItemName))
            {
                purchaseConfirmations++;
                deadline = timeProvider.GetUtcNow().AddSeconds(30);
                return;
            }
            if (timeProvider.GetUtcNow() < deadline)
                return;
            Halt($"PURCHASE OUTCOME UNKNOWN for {currentOrder.ItemName}: a request was submitted, " +
                 $"but inventory did not confirm it before the timeout ({purchaseConfirmations} confirmation " +
                 "prompt(s) answered). Procurement stopped to prevent a duplicate buy.");
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
            pricingRule.MinimumMarginPercent, RequiredPurchaseRoi(actual));
        pricingRule.MinimumPrice = Math.Max(pricingRule.MinimumPrice,
            ProcurementPriceSafety.MinimumResalePrice(pricingRule.CostBasis,
                pricingRule.MinimumMarginPercent, configuration.Current.ProcurementMinimumProfitPerUnit));
        actual = actual with { TargetSalePrice = Math.Max(actual.TargetSalePrice, pricingRule.MinimumPrice) };
        ledger.RecordPurchase(actual, stackSize);
        configuration.Current.PerItemRules[actual.ItemId] = pricingRule;
        configuration.Save();
        gilSpent += (uint)Math.Min(purchaseCost, uint.MaxValue - gilSpent);
        confirmedPurchases++;
        if (priorityShopping && stockHuntScanning && stockHuntWorldIndex == 0 && homePrices.TryGetValue(actual.ItemId, out var home))
            homePrices[actual.ItemId] = home.Where(x => x.ListingId != actual.ListingId).ToArray();
        scoutListings.RemoveAll(x => x.WorldName == actual.WorldName && x.ListingId == actual.ListingId);
        purchasedSlotsByItem[actual.ItemId] = purchasedSlotsByItem.GetValueOrDefault(actual.ItemId) + 1;
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

    private decimal RequiredPurchaseRoi(ProcurementOrder order) => order.IsFillOrder
        ? configuration.Current.ProcurementFillRoiPercent : configuration.Current.ProcurementMinimumRoiPercent;

    private bool RetryListingRequest(string itemName)
    {
        if (listingRequestAttempts >= 3)
            return false;
        listingRequestAttempts++;
        market.ResetListingRequest();
        nextActionAt = timeProvider.GetUtcNow().AddSeconds(listingRequestAttempts - 1);
        deadline = nextActionAt.AddSeconds(6 * listingRequestAttempts);
        detail = $"Retrying the live search for {itemName} on {WorldName} (attempt {listingRequestAttempts}/3). No purchase has been submitted for this order.";
        log.Add(AutomationLogLevel.Warning, detail);
        return true;
    }

    private void AdvanceOrder()
    {
        if (priorityShopping && stockHuntScanning)
        {
            currentOrder = null;
            currentLiveListing = null;
            AdvanceStockHuntRule();
            return;
        }
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
            SubmitMarketTravelAttempt(objectName, returningHome);
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
            FailDestination($"Could not reach a {objectName} after 3 Lifestream market-area attempts. " +
                 $"Check that '{configuration.Current.MarketBoardTravelCommand}' works in chat and that Lifestream is enabled.");
            return;
        }

        SubmitMarketTravelAttempt(objectName, returningHome);
    }

    private void SubmitMarketTravelAttempt(string objectName, bool returningHome)
    {
        localTravelObservedBusy = false;
        localTravelAttempts++;
        if (!TryExecuteMarketTravel(returningHome))
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

    private bool TryExecuteMarketTravel(bool returningHome)
    {
        // The summoning-bell leg can be pointed somewhere quieter than the market
        // hub. Empty keeps the original behaviour of reusing the market command.
        var bell = configuration.Current.SummoningBellTravelCommand.Trim();
        var command = returningHome && !string.IsNullOrWhiteSpace(bell)
            ? bell
            : configuration.Current.MarketBoardTravelCommand.Trim();
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
            repricing.IsActive || repricing.RequiresManualRestart || !playerState.IsLoaded)
            return;

        // Never buy against the fallback UI target before an actual retainer pass has
        // reported real capacity - but a completed pass reporting zero free slots is
        // still real. AvailablePurchaseSlots decides from there, and it allows a bag
        // buffer, so full retainers keep stocking up for the next sale.
        if (repricing.LastKnownFreeSaleSlots is null ||
            ResaleStockPolicy.SpendableGil(market.Gil, configuration.Current.ProcurementTravelReserve, true, 0) == 0)
            return;
        if (!configuration.Current.ContinueShoppingWhenStocked &&
            (AvailablePurchaseSlots() <= 0 || market.FreeInventorySlots <= configuration.Current.ProcurementInventoryReserve || ShoppingBudget == 0))
            return;

        if (configuration.Current.LiveWorldStockHuntEnabled)
        {
            if (configuration.Current.AllowAutomaticPurchases &&
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
        var newlyAvailableCapacity = freeSaleSlots is not null &&
                                     PlannedSaleSlots(freeSaleSlots.Value) != lastScannedFreeSaleSlots;
        var incomeArrived = ShoppingBudget > lastScannedBudget;
        if (!newlyAvailableCapacity && !incomeArrived && timeProvider.GetUtcNow() < nextAutomaticScan)
            return;
        nextAutomaticScan = timeProvider.GetUtcNow().AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        StartScan(ProcurementRunMode.AutomaticPurchase);
    }

    private bool HasLowCuratedStock() => ResaleStockPolicy.SelectTourRules(
        configuration.Current.ProcurementRules, IsBelowStockThreshold,
        configuration.Current.LiveWorldStockHuntMaximumItems).Count > 0;

    // Count only the qualities a rule actually trades, so a normal-quality food or
    // potion is not judged by an HQ stock level it will never have.
    private bool IsBelowStockThreshold(ProcurementRule rule)
    {
        var held = 0;
        foreach (var quality in new[] { false, true })
            if (ResaleStockPolicy.BuyableQuality(rule, quality, configuration.Current.BuyHighQualityOnly))
                held += market.GetInventoryCount(rule.ItemId, quality);
        return held < configuration.Current.LiveWorldStockThresholdPerItem;
    }

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
        ownsRetainerPause = false;
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
            FailDestination($"Timed out approaching or interacting with {objectName}.");
        return true;
    }

    private void CheckTimeout(string message)
    {
        if (timeProvider.GetUtcNow() >= deadline)
            FailDestination(message);
    }

    private int AvailablePurchaseSlots() => PlannedSaleSlots(repricing.LastKnownFreeSaleSlots ?? 0);

    // With reinvestment on, the wallet itself is the budget - the point is to
    // compound sales into the next trip - and the per-trip cap only applies when
    // the user turns reinvestment off. The travel reserve is always withheld so a
    // purchase cannot strand the character without teleport fare.
    private uint SpendableGil(bool newTrip = false)
    {
        var config = configuration.Current;
        var available = ResaleStockPolicy.SpendableGil(market.Gil, config.ProcurementTravelReserve,
            config.ReinvestAvailableGil, config.ProcurementBudget, newTrip ? 0 : gilSpent);
        var bags = CollectBagStock();
        if (Math.Min(repricing.LastKnownFreeSaleSlots ?? 0, config.ProcurementTargetSaleSlots) > bags.Sum(x => x.SaleSlots))
            return available;

        var wallet = ResaleStockPolicy.SpendableGil(market.Gil, config.ProcurementTravelReserve, true, 0);
        var bufferCost = bags.Aggregate(0UL, (cost, item) => cost +
            (ulong)item.Quantity * config.GetEffectiveRule(item.ItemId).CostBasis);
        return Math.Min(available, ResaleStockPolicy.BufferSpendableGil(wallet, bufferCost, config.ProcurementBufferGilPercent));
    }

    // Stock the planner must count against its per-item limits: stacks already
    // listed on the retainers plus everything held in the bags. Without this a
    // cheap item is re-bought every trip until it crowds out everything else.
    // Each holding also carries its portfolio tier, so what is already listed
    // counts toward the preferred target and against the opportunistic cap.
    private IReadOnlyList<StockExposure> CollectOwnedStock() =>
        repricing.ListedStock.Concat(CollectBagStock()).Select(WithPortfolioTier).ToArray();

    /// <summary>
    /// Tier a holding we already own. Profit is sunk on owned stock, so only the
    /// pinned flag, the observed sales rate and the value of the occupied slot
    /// decide. An item with no known market data counts as opportunistic, which is
    /// what makes a retainer full of listed dye block buying more of it.
    /// </summary>
    private StockExposure WithPortfolioTier(StockExposure stock)
    {
        var rule = configuration.Current.ProcurementRules
            .FirstOrDefault(x => x.ItemId == stock.ItemId && x.ItemId != 0);
        var slots = Math.Max(1, stock.SaleSlots);
        var unitPrice = homePrices.TryGetValue(stock.ItemId, out var listings)
            ? HomePriceReference.Summarize(stock.ItemId, string.Empty, stock.IsHighQuality, listings).Reference
            : configuration.Current.GetEffectiveRule(stock.ItemId).CostBasis;
        return stock with
        {
            Tier = PortfolioPolicy.ClassifyHolding(
                rule?.PreferredStock == true && rule.LiquidateOnly != true,
                HomeSalesPerDay(stock.ItemId, stock.IsHighQuality),
                (ulong)unitPrice * stock.Quantity / (ulong)slots),
        };
    }

    /// <summary>
    /// The denominator for the portfolio percentages: the whole trading position,
    /// not one shopping run. Without this a single trip with three free slots would
    /// treat one dye as a third of the portfolio.
    /// </summary>
    private int PortfolioCapacitySlots() => Math.Max(
        configuration.Current.ProcurementTargetSaleSlots,
        (repricing.LastKnownFreeSaleSlots ?? 0) + repricing.ListedStock.Sum(x => x.SaleSlots));

    /// <summary>What the current portfolio looks like against its targets.</summary>
    public PortfolioAllocationSummary PortfolioSummary
    {
        get
        {
            var owned = CollectOwnedStock().GroupBy(x => x.Tier)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.SaleSlots));
            return PortfolioPolicy.Summarize(owned, 0, PortfolioCapacitySlots(), configuration.Current.PortfolioGates);
        }
    }

    /// <summary>The last plan's reasoning, newest first, for the dashboard.</summary>
    public IReadOnlyList<PortfolioDecision> PortfolioDecisions => portfolioDecisions;

    // Explain the allocation in the session log. One line per item and quality,
    // so a hundred rejected listings of the same dye do not bury the reasoning.
    private void LogPortfolioDecisions(string prefix, ProcurementPlan plan)
    {
        var rows = plan.DecisionLog
            .GroupBy(x => (x.ItemId, x.IsHighQuality, x.Selected))
            .Select(g => g.OrderByDescending(x => x.ExpectedProfit).First())
            .OrderByDescending(x => x.Selected)
            .ThenBy(x => PortfolioPolicy.Rank(x.Tier))
            .ThenByDescending(x => x.ExpectedProfit)
            .ToArray();
        portfolioDecisions = rows;
        if (rows.Length == 0)
            return;
        log.Add(AutomationLogLevel.Information, $"{prefix} PORTFOLIO: {plan.Summary}");
        foreach (var row in rows.Take(40))
            log.Add(AutomationLogLevel.Information, $"{prefix} {row}");
    }

    private IReadOnlyList<StockExposure> CollectBagStock()
    {
        var config = configuration.Current;
        // Sale-only dyes, materia and ethers can produce hundreds of small future
        // listings. They occupy real inventory slots but are not the trading
        // buffer and must not reserve every investment opportunity.
        var rules = config.ProcurementRules.Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .DistinctBy(x => x.ItemId).ToDictionary(x => x.ItemId);
        var pending = ledger.Snapshot().Where(x => x.PendingQuantity > 0 &&
            (rules.ContainsKey(x.ItemId) || (!x.IsBagStock && !config.ProcurementRules.Any(r => r.ItemId == x.ItemId && r.LiquidateOnly))))
            .ToDictionary(x => (x.ItemId, x.IsHighQuality));
        // Scan the four bags once, instead of searching the whole inventory for
        // every seeded dye/materia rule on every controller tick.
        var holdings = retainerListings.ReadBagListingCandidates()
            .Where(x => rules.TryGetValue(x.ItemId, out var rule) &&
                ResaleStockPolicy.BuyableQuality(rule, x.IsHighQuality, config.BuyHighQualityOnly))
            .GroupBy(x => (x.ItemId, x.IsHighQuality))
            .ToDictionary(x => x.Key, x => x.Sum(y => (long)y.Quantity));
        var keys = holdings.Keys.Concat(pending.Keys).Distinct();
        var stock = new List<StockExposure>();
        foreach (var key in keys)
        {
            rules.TryGetValue(key.ItemId, out var rule);
            pending.TryGetValue(key, out var entry);
            var reserve = ResaleStockPolicy.BagReserve(rule, rule?.ItemName ?? entry?.ItemName ?? string.Empty,
                (uint)config.BagListingReservePerItem);
            var held = (uint)Math.Clamp(holdings.GetValueOrDefault(key) - reserve, 0L, uint.MaxValue);
            // The ledger describes the same inventory, not another pile of stock.
            var quantity = Math.Max(held, entry?.PendingQuantity ?? 0);
            var size = Math.Max(1, rule?.TargetStackSize ?? entry?.TargetStackSize ?? 99);
            var slots = (int)(((long)quantity + size - 1) / size);
            // Cap by what this item may actually occupy on the retainers. A pile of
            // 999 materia is not 50 stacks of trading buffer when its rule allows one
            // sale slot; counting the raw quantity made a full-looking buffer out of
            // a few deep stacks and stopped shopping entirely.
            if (rule is not null && rule.MaximumSaleSlots > 0)
                slots = Math.Min(slots, rule.MaximumSaleSlots);
            if (entry is not null)
                slots = Math.Max(slots, (int)Math.Min(entry.PendingQuantity, (long)entry.MaximumListingSlots - entry.ListingsCreated));
            if (quantity > 0)
                stock.Add(new(key.ItemId, key.IsHighQuality, quantity, slots));
        }
        return stock;
    }

    // Every capacity decision - planning, the pre-purchase guard, and the
    // scheduler's "capacity changed" trigger - must agree on this number.
    // Comparing two different definitions is what made the scheduler rescan
    // Universalis continuously whenever any purchased stock was still unlisted.
    // Beyond the free retainer slots, keep buying a small buffer of stacks that sit
    // in the bags ready to list the moment something sells. Without it, full
    // retainers stop shopping entirely and every sale waits a whole trip to refill.
    private int PlannedSaleSlots(int freeSaleSlots)
    {
        var free = Math.Min(freeSaleSlots, configuration.Current.ProcurementTargetSaleSlots);
        var held = ResaleBagSlots;
        // Fill real vacancies first. Buffer shopping gets a separate, smaller
        // budget once bags can cover those vacancies.
        if (free > held)
            return free - held;
        if (configuration.Current.ContinueShoppingWhenStocked)
            return Math.Min(Math.Max(0, free + ComfortableStockTarget - held),
                Math.Max(0, (int)market.FreeInventorySlots - configuration.Current.ProcurementInventoryReserve));
        return Math.Max(0, free + configuration.Current.ProcurementBagBufferStacks - held);
    }

    private IReadOnlyList<ProcurementRule> ShoppingRules(IReadOnlyList<ProcurementRule> source)
    {
        if (!configuration.Current.ContinueShoppingWhenStocked ||
            Math.Min(repricing.LastKnownFreeSaleSlots ?? 0, configuration.Current.ProcurementTargetSaleSlots) > ResaleBagSlots)
            return source;
        // A fully listed item still needs a few bag replacements. Limit spare
        // stock to 1-3 sale stacks per item instead of letting one cheap item fill
        // the entire buffer. Quantity/weekly-demand limits still count all stock.
        return source.Select(rule =>
        {
            var listed = repricing.ListedStock.Where(x => x.ItemId == rule.ItemId).Sum(x => x.SaleSlots);
            var copy = rule.Clone();
            copy.MaximumSaleSlots = listed + Math.Min(rule.MaximumSaleSlots, ResaleStockPolicy.ComfortableItemTarget(listed));
            return copy;
        }).ToArray();
    }

    private static IReadOnlyList<ProcurementMarketItem> ShoppingMarkets(IReadOnlyList<ProcurementMarketItem> items) =>
        items.Select(x => x with { Listings = x.Listings.Where(y => ProcurementTravelPolicy.CanShopOnWorld(y.WorldName)).ToArray() }).ToArray();

    private bool StopUnproductiveTour()
    {
        if (consecutiveFailedWorlds < 2)
            return false;
        FinishShopping("Live tour stopped: two consecutive worlds returned no completed item searches. Check the market-search and travel log before restarting.");
        return true;
    }

    private void FinishShopping(string reason)
    {
        log.Add(AutomationLogLevel.Warning, reason);
        routeOutcome = reason;
        stockHuntScanning = false;
        lifestream.Abort();
        vnavmesh.Stop();
        currentOrder = null;
        currentLiveListing = null;
        ReturnHome();
    }

    private void FailDestination(string reason)
    {
        if (stockHuntScanning)
        {
            SkipStockHuntWorld(reason);
            return;
        }
        if (activeRunMode != ProcurementRunMode.None && State is
            ProcurementState.WaitingBeforeWorldTravel or ProcurementState.WaitingForWorld or
            ProcurementState.WaitingAfterWorldArrival or ProcurementState.WaitingForMarketBoardTravel or
            ProcurementState.FindingMarketBoard or ProcurementState.MovingToMarketBoard or ProcurementState.WaitingForMarketBoard)
        {
            log.Add(AutomationLogLevel.Warning, $"SKIPPED WORLD {WorldName}: {reason}");
            lifestream.Abort();
            vnavmesh.Stop();
            if (worldIndex < worldGroups.Count)
                skippedPurchases += Math.Max(0, worldGroups[worldIndex].Count() - orderIndex);
            currentOrder = null;
            currentLiveListing = null;
            worldIndex++;
            orderIndex = 0;
            TravelToCurrentWorld();
            return;
        }
        HaltForRetry(reason);
    }

    // A stopped route otherwise stays stopped for the rest of the session, so a
    // single passing failure silently ends unattended shopping. Once the retry
    // delay has passed and the character is parked at a summoning bell again,
    // return to Idle and let the scheduler decide whether to run.
    private void TryResumeAfterStop()
    {
        if (timeProvider.GetUtcNow() < resumeStoppedRouteAt || lifestream.IsBusy ||
            !playerState.IsLoaded)
            return;
        if (retryReturnHome && configuration.Current.AutomaticProcurementEnabled)
        {
            if (IsStartBlocked?.Invoke() == true)
                return;
            retryReturnHome = false;
            resumeStoppedRouteAt = DateTimeOffset.MaxValue;
            activeRunMode = ProcurementRunMode.AutomaticPurchase;
            ReturnHome();
            return;
        }
        if (!retainerListings.IsRetainerListOpen)
            return;
        if (IsStartBlocked?.Invoke() == true)
            return;
        resumeStoppedRouteAt = DateTimeOffset.MaxValue;
        State = ProcurementState.Idle;
        detail = "Ready to retry procurement after the previous stop.";
        log.Add(AutomationLogLevel.Information, detail);
        if (ownsRetainerPause && configuration.Current.AutomationEnabled && configuration.Current.AutomaticProcurementEnabled)
        {
            ownsRetainerPause = false;
            repricing.StartNow();
        }
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
