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
    FindingMarketBoard,
    MovingToMarketBoard,
    WaitingForMarketBoard,
    WaitingForListings,
    WaitingForPurchase,
    WaitingForHomeWorld,
    WaitingAfterHomeArrival,
    FindingSummoningBell,
    MovingToSummoningBell,
    WaitingForSummoningBell,
    Completed,
    Halted,
    Faulted,
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
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly ICommandManager commandManager;
    private readonly IRetainerListingService retainerListings;
    private readonly IUniversalisService universalis;
    private readonly IProcurementPlannerService planner;
    private readonly IMarketPurchaseService market;
    private readonly IVnavmeshService vnavmesh;
    private readonly ProcurementLedger ledger;
    private readonly AutomationController repricing;
    private readonly ConfigurationService configuration;
    private readonly AutomationLog log;

    private CancellationTokenSource? cancellation;
    private Task<IReadOnlyList<ProcurementMarketItem>>? scanTask;
    private List<IGrouping<string, ProcurementOrder>> worldGroups = [];
    private ProcurementOrder? currentOrder;
    private LivePurchaseListing? currentLiveListing;
    private DateTimeOffset deadline;
    private DateTimeOffset nextActionAt;
    private DateTimeOffset nextAutomaticScan;
    private int worldIndex;
    private int orderIndex;
    private int inventoryBefore;
    private bool executeAfterScan;
    private string homeWorld = string.Empty;
    private string detail = "Procurement is idle.";
    private uint gilSpent;

    public ProcurementController(
        IFramework framework,
        IPlayerState playerState,
        ICommandManager commandManager,
        IRetainerListingService retainerListings,
        IUniversalisService universalis,
        IProcurementPlannerService planner,
        IMarketPurchaseService market,
        IVnavmeshService vnavmesh,
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
        this.ledger = ledger;
        this.repricing = repricing;
        this.configuration = configuration;
        this.log = log;
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        framework.Update += OnFrameworkUpdate;
    }

    public ProcurementState State { get; private set; } = ProcurementState.Idle;
    public ProcurementPlan Plan { get; private set; } = ProcurementPlan.Empty;
    public bool IsActive => State is not (ProcurementState.Idle or ProcurementState.PlanReady or ProcurementState.Completed or ProcurementState.Halted or ProcurementState.Faulted);
    public ProcurementStatus Status => new(
        State, detail, Math.Min(orderIndex + 1, Plan.Orders.Count), Plan.Orders.Count, gilSpent,
        configuration.Current.AutomaticProcurementEnabled ? nextAutomaticScan : null);

    public void ScanNow() => StartScan(false);

    public void RunNow()
    {
        if (Plan.Orders.Count == 0)
            StartScan(true);
        else
            BeginExecution();
    }

    public void Halt(string reason = "Procurement stopped by user.")
    {
        cancellation?.Cancel();
        scanTask = null;
        vnavmesh.Stop();
        State = ProcurementState.Halted;
        detail = reason;
        log.Add(AutomationLogLevel.Warning, reason);
    }

    private void StartScan(bool execute)
    {
        if (IsActive)
            return;
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
        executeAfterScan = execute;
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
                if (IsOnWorld(WorldName))
                    Delay(ProcurementState.WaitingAfterWorldArrival, "Waiting for the destination world to settle.", 5_000);
                else
                    CheckTimeout($"Timed out travelling to {WorldName}.");
                break;
            case ProcurementState.WaitingAfterWorldArrival:
                if (DelayElapsed()) TravelToMarketBoard();
                break;
            case ProcurementState.FindingMarketBoard:
                FindAndApproach("Market Board", ProcurementState.MovingToMarketBoard, ProcurementState.WaitingForMarketBoard);
                break;
            case ProcurementState.MovingToMarketBoard:
                PollApproach("Market Board", ProcurementState.WaitingForMarketBoard);
                break;
            case ProcurementState.WaitingForMarketBoard:
                if (market.IsMarketBoardOpen)
                    BeginCurrentOrder();
                else
                    CheckTimeout("Timed out opening the Market Board.");
                break;
            case ProcurementState.WaitingForListings:
                PollListings();
                break;
            case ProcurementState.WaitingForPurchase:
                PollPurchase();
                break;
            case ProcurementState.WaitingForHomeWorld:
                if (IsOnWorld(homeWorld))
                    Delay(ProcurementState.WaitingAfterHomeArrival, "Waiting for the home world to settle.", 5_000);
                else
                    CheckTimeout($"Timed out returning to {homeWorld}.");
                break;
            case ProcurementState.WaitingAfterHomeArrival:
                if (DelayElapsed()) TravelToSummoningBell();
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
                    Complete("Purchasing finished; the retainer run will distribute and list purchased stacks.");
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
        var freeInventorySlots = Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve);
        Plan = planner.BuildPlan(new(
            scanTask.Result,
            config.ProcurementRules,
            Math.Min(config.ProcurementBudget, market.Gil),
            Math.Min(freeSaleSlots, config.ProcurementTargetSaleSlots),
            freeInventorySlots,
            config.ProcurementMinimumRoiPercent,
            config.ProcurementMinimumProfitPerUnit));
        scanTask = null;
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(config.ProcurementIntervalMinutes);
        State = ProcurementState.PlanReady;
        detail = Plan.Orders.Count == 0
            ? "No deals passed the volume, margin, budget, bag-slot, and sale-slot guards."
            : $"Plan ready: {Plan.Orders.Count} stack(s), {Plan.TotalCost:N0} gil, about {Plan.ExpectedProfit:N0} gil expected profit.";
        log.Add(AutomationLogLevel.Information, detail);
        if (executeAfterScan && Plan.Orders.Count > 0)
            BeginExecution();
    }

    private void BeginExecution()
    {
        if (!configuration.Current.AllowAutomaticPurchases)
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

        repricing.Halt("Paused while procurement runs.");
        market.CloseRetainerList();
        worldGroups = Plan.Orders.GroupBy(x => x.WorldName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Sum(y => (long)y.ExpectedProfit)).ToList();
        worldIndex = 0;
        orderIndex = 0;
        gilSpent = 0;
        TravelToCurrentWorld();
    }

    private string WorldName => worldIndex < worldGroups.Count ? worldGroups[worldIndex].Key : string.Empty;

    private void TravelToCurrentWorld()
    {
        market.CloseMarketBoard();
        if (worldIndex >= worldGroups.Count)
        {
            ReturnHome();
            return;
        }
        if (IsOnWorld(WorldName))
        {
            Delay(ProcurementState.WaitingAfterWorldArrival, $"Preparing to visit {WorldName}'s market board.", 2_000);
            return;
        }
        if (!commandManager.ProcessCommand($"/li {WorldName}"))
        {
            Halt("Lifestream did not accept the world-travel command.");
            return;
        }
        Wait(ProcurementState.WaitingForWorld, $"Travelling to {WorldName} with Lifestream.", 180);
    }

    private void TravelToMarketBoard()
    {
        commandManager.ProcessCommand(configuration.Current.MarketBoardTravelCommand);
        Wait(ProcurementState.FindingMarketBoard, $"Finding {WorldName}'s market board.", 90);
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
            Delay(ProcurementState.WaitingAfterHomeArrival, "Preparing to return to the summoning bell.", 2_000);
            return;
        }
        if (!commandManager.ProcessCommand($"/li {homeWorld}"))
        {
            Halt("Lifestream did not accept the return-home command.");
            return;
        }
        Wait(ProcurementState.WaitingForHomeWorld, $"Returning to {homeWorld}.", 180);
    }

    private void TravelToSummoningBell()
    {
        commandManager.ProcessCommand(configuration.Current.MarketBoardTravelCommand);
        Wait(ProcurementState.FindingSummoningBell, "Finding a summoning bell near the market board.", 90);
    }

    private void FindAndApproach(string objectName, ProcurementState movingState, ProcurementState openedState)
    {
        var position = market.FindNearest(objectName);
        if (!position.HasValue)
        {
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
        CheckTimeout($"Timed out walking to {objectName}.");
    }

    private void TryAutomaticStart()
    {
        if (!configuration.Current.AutomaticProcurementEnabled || DateTimeOffset.UtcNow < nextAutomaticScan ||
            !retainerListings.IsRetainerListOpen || repricing.IsActive)
            return;
        nextAutomaticScan = DateTimeOffset.UtcNow.AddMinutes(configuration.Current.ProcurementIntervalMinutes);
        StartScan(true);
    }

    private bool IsOnWorld(string world) => playerState.IsLoaded && string.Equals(
        playerState.CurrentWorld.Value.Name.ToString(), world, StringComparison.OrdinalIgnoreCase);

    private void Complete(string message)
    {
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
    }
}
