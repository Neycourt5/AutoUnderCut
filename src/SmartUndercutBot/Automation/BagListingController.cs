using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public enum BagListingState
{
    Idle,
    ConsolidatingBags,
    ScanningPrices,
    QueuePrepared,
    WaitingForVerification,
    Completed,
    Failed,
}

public sealed record BagListingStatus(BagListingState State, string Detail);

public sealed record CuratedBagStock(
    uint ItemId,
    string ItemName,
    uint TotalQuantity,
    uint ReservedQuantity,
    uint ListableQuantity,
    int StackCount,
    uint EffectiveFloor,
    uint? SuggestedPrice,
    bool IsHighQuality,
    int TargetStackSize,
    bool Eligible,
    int PhysicalBagSlots);

public sealed class BagListingController : IDisposable
{
    private static readonly HashSet<string> CuratedItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Grade 4 Gemdraught of Strength",
        "Grade 4 Gemdraught of Dexterity",
        "Grade 4 Gemdraught of Intelligence",
        "Grade 4 Gemdraught of Mind",
        "Caramel Popcorn",
    };

    private readonly IFramework framework;
    private readonly ICommandManager commandManager;
    private readonly IPlayerState playerState;
    private readonly IRetainerListingService retainerListings;
    private readonly IUniversalisService universalis;
    private readonly IPricingStrategyService pricing;
    private readonly ConfigurationService configuration;
    private readonly ProcurementLedger ledger;
    private readonly AutomationController automation;
    private readonly ProcurementController procurement;
    private readonly AutomationLog log;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<(uint, bool), uint> plannedPrices = [];
    private IReadOnlyList<BagListingCandidate> candidates = [];
    private CancellationTokenSource? scanCancellation;
    private Task<IReadOnlyList<ProcurementMarketItem>>? scanTask;
    private PendingAutoListing? pending;
    private DateTimeOffset deadline;
    private DateTimeOffset nextAutomaticAttempt;
    private DateTimeOffset bagSortReadyAt;
    private bool automaticStartSuspended;
    private bool awaitingFillCompletion;
    private int? lastAttemptFreeSaleSlots;

    public BagListingController(
        IFramework framework,
        ICommandManager commandManager,
        IPlayerState playerState,
        IRetainerListingService retainerListings,
        IUniversalisService universalis,
        IPricingStrategyService pricing,
        ConfigurationService configuration,
        ProcurementLedger ledger,
        AutomationController automation,
        ProcurementController procurement,
        AutomationLog log,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.commandManager = commandManager;
        this.playerState = playerState;
        this.retainerListings = retainerListings;
        this.universalis = universalis;
        this.pricing = pricing;
        this.configuration = configuration;
        this.ledger = ledger;
        this.automation = automation;
        this.procurement = procurement;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        nextAutomaticAttempt = this.timeProvider.GetUtcNow();
        Refresh();
        framework.Update += OnFrameworkUpdate;
    }

    public BagListingStatus Status { get; private set; } = new(BagListingState.Idle,
        "Only curated HQ raid consumables are eligible for automatic listing.");
    public IReadOnlyList<BagListingCandidate> Candidates => candidates;
    public IReadOnlyList<CuratedBagStock> Stock => BuildStockSummary();
    public bool IsBusy => State is BagListingState.ConsolidatingBags or BagListingState.ScanningPrices or BagListingState.WaitingForVerification;
    public bool IsRetainerSellListOpen => retainerListings.IsSellListOpen;
    public bool IsRetainerListOpen => retainerListings.IsRetainerListOpen;
    public DateTimeOffset? LastBagScanAt { get; private set; }
    public bool IsAutomaticRunDue => !automaticStartSuspended &&
        configuration.Current.AutomaticCuratedBagListingEnabled && configuration.Current.AllowAutomaticListing &&
        !automation.RequiresManualRestart &&
        (timeProvider.GetUtcNow() >= nextAutomaticAttempt ||
         automation.LastKnownFreeSaleSlots > lastAttemptFreeSaleSlots) && !automation.IsActive &&
        retainerListings.IsRetainerListOpen && automation.LastKnownFreeSaleSlots is > 0;
    private BagListingState State => Status.State;

    public void ResumeAutomatic()
    {
        if (IsBusy)
            return;
        automaticStartSuspended = false;
        awaitingFillCompletion = false;
        lastAttemptFreeSaleSlots = null;
        nextAutomaticAttempt = timeProvider.GetUtcNow();
        Refresh();
    }

    public void Refresh()
    {
        if (IsBusy)
            return;
        RefreshCandidatesOnly();
        var distinctItems = candidates.Select(x => x.ItemId).Distinct().Count();
        Status = new(BagListingState.Idle,
            distinctItems == 0
                ? "No marketable bag items were found."
                : $"Scanned {distinctItems} marketable item type(s). Only enabled resale stock will be listed.");
    }

    public void PrepareAutomaticRun()
    {
        if (IsBusy)
            return;
        if (!retainerListings.IsRetainerListOpen)
        {
            Fail("Open the summoning-bell retainer list before starting automatic bag listing.");
            return;
        }
        if (automation.IsActive || procurement.IsActive)
        {
            Fail("Wait for the current retainer or procurement operation to finish.");
            return;
        }
        if (!configuration.Current.AllowAutomaticListing)
        {
            Fail("Arm automatic listing before preparing a bag-listing run.");
            return;
        }

        automaticStartSuspended = false;

        ScheduleNextAttempt();
        if (commandManager.ProcessCommand("/isort execute inventory"))
        {
            bagSortReadyAt = this.timeProvider.GetUtcNow().AddMilliseconds(900);
            Status = new(BagListingState.ConsolidatingBags,
                "Sorting bags before calculating reserves and resale stacks.");
            log.Add(AutomationLogLevel.Information, Status.Detail);
            return;
        }

        BeginPriceScan();
    }

    private void BeginPriceScan()
    {
        Status = new(BagListingState.Idle, "Inventory consolidation finished; refreshing curated stock.");
        Refresh();
        var rules = BuildScanRules();
        if (rules.Count == 0)
        {
            Status = new(BagListingState.Completed,
                "Nothing is ready to list after item rules, reserves, and existing retainer stock are counted.");
            ScheduleNextAttempt();
            return;
        }
        if (!playerState.IsLoaded)
        {
            Fail("The current world is not available yet.");
            return;
        }

        var world = playerState.CurrentWorld.Value.Name.ToString();
        if (string.IsNullOrWhiteSpace(world))
        {
            Fail("Could not determine the current world for pricing.");
            return;
        }

        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        scanCancellation = new CancellationTokenSource();
        scanTask = universalis.ScanAsync(rules, world, scanCancellation.Token);
        deadline = timeProvider.GetUtcNow().AddSeconds(150);
        Status = new(BagListingState.ScanningPrices,
            $"Reading current {world} prices for {rules.Count} curated HQ item(s).");
        log.Add(AutomationLogLevel.Information, Status.Detail);
    }

    public bool List(BagListingCandidate candidate, uint quantity, uint unitPrice)
    {
        if (IsBusy || automation.IsActive || procurement.IsActive)
            return false;
        if (!retainerListings.TryListBagItem(candidate, quantity, unitPrice, out pending, out var message) ||
            pending is null)
        {
            Fail(message, $"BAG LIST FAILED {candidate.ItemName}: {message}");
            return false;
        }

        deadline = this.timeProvider.GetUtcNow().AddSeconds(10);
        Status = new(BagListingState.WaitingForVerification, message + " Waiting for retainer verification.");
        log.Add(AutomationLogLevel.Information, $"BAG LIST SUBMITTED {message}");
        return true;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Halt($"Bag listing failed: {ex.Message}");
        }
    }

    public void Halt(string reason = "Bag listing stopped by user.")
    {
        scanCancellation?.Cancel();
        scanTask = null;
        pending = null;
        automaticStartSuspended = true;
        ledger.ClearBagStockQueue();
        Fail(reason);
    }

    private void Tick()
    {
        if (State == BagListingState.ConsolidatingBags)
        {
            if (!configuration.Current.AllowAutomaticListing || !retainerListings.IsRetainerListOpen)
            {
                Fail("Bag filling paused because listing was disabled or the summoning bell closed.");
                ScheduleNextAttempt();
                return;
            }
            if (this.timeProvider.GetUtcNow() >= bagSortReadyAt)
                BeginPriceScan();
            return;
        }
        if (State == BagListingState.ScanningPrices)
        {
            PollPriceScan();
            return;
        }
        if (State == BagListingState.WaitingForVerification)
        {
            PollManualVerification();
            return;
        }
        TryAutomaticStart();
    }

    private void PollPriceScan()
    {
        if (!configuration.Current.AllowAutomaticListing)
        {
            Halt("Automatic listing was disarmed while prices were loading.");
            return;
        }
        if (scanTask is null || !scanTask.IsCompleted)
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                scanCancellation?.Cancel();
                scanTask = null;
                Fail("Bag-listing price scan timed out; stock remains in inventory.");
                ScheduleNextAttempt();
            }
            return;
        }
        if (scanTask.IsCanceled || scanTask.IsFaulted)
        {
            Fail(scanTask.Exception?.GetBaseException().Message ?? "The Universalis price scan was cancelled.");
            scanTask = null;
            ScheduleNextAttempt();
            return;
        }
        if (!retainerListings.IsRetainerListOpen)
        {
            Fail("The summoning-bell retainer list closed before the price plan was ready.");
            scanTask = null;
            ScheduleNextAttempt();
            return;
        }

        QueuePricedStock(scanTask.Result);
        scanTask = null;
    }

    private void QueuePricedStock(IReadOnlyList<ProcurementMarketItem> markets)
    {
        plannedPrices.Clear();
        ledger.ClearBagStockQueue();
        var existingProcurement = ledger.Snapshot()
            .Where(x => !x.IsBagStock && x.PendingQuantity > 0)
            .Select(x => (x.ItemId, x.IsHighQuality))
            .ToHashSet();
        var queuedStacks = 0;
        foreach (var stock in BuildStockSummary())
        {
            if (stock.StackCount == 0 || existingProcurement.Contains((stock.ItemId, stock.IsHighQuality)))
                continue;
            var market = markets.FirstOrDefault(x => x.ItemId == stock.ItemId);
            var listings = market?.Listings
                .Select(x => new MarketListing(x.PricePerUnit, x.Quantity, x.IsHighQuality, RetainerId: x.RetainerId))
                .ToArray() ?? [];
            var historicalMedian = Median(market?.RecentSales.Where(x => x.IsHighQuality == stock.IsHighQuality).Select(x => x.PricePerUnit) ?? []);
            var rule = configuration.Current.GetEffectiveRule(stock.ItemId);
            rule.QualityFilter = stock.IsHighQuality ? QualityFilterMode.HighQualityOnly : QualityFilterMode.NormalQualityOnly;
            var placeholder = new RetainerListing(
                0, "New bag listing", -1, stock.ItemId, stock.ItemName, (uint)stock.TargetStackSize,
                PricingStrategyService.MaximumListingPrice, stock.IsHighQuality, rule.CostBasis);
            var decision = pricing.Evaluate(new PricingContext(
                placeholder,
                new MarketSnapshot(stock.ItemId, this.timeProvider.GetUtcNow(), listings, historicalMedian, true),
                rule,
                retainerListings.OwnedRetainerIds));
            uint? target = decision.ShouldUpdate && decision.TargetPrice is { } marketTarget &&
                           MarketPriceSafety.IsSafeAutomaticUnitPrice(stock.ItemName, marketTarget, (uint)stock.TargetStackSize)
                ? marketTarget
                : null;

            if (!target.HasValue && ResaleStockPolicy.IsCuratedConsumable(stock.ItemName) &&
                automation.TryGetKnownSafePrice(stock.ItemId, out var knownPrice))
                target = Math.Max(stock.EffectiveFloor, knownPrice);
            if (!target.HasValue && historicalMedian is { } median &&
                MarketPriceSafety.IsSafeAutomaticUnitPrice(stock.ItemName, median, (uint)stock.TargetStackSize))
                target = Math.Max(stock.EffectiveFloor, median);

            if (!target.HasValue || !MarketPriceSafety.IsSafeAutomaticUnitPrice(stock.ItemName, target.Value, (uint)stock.TargetStackSize))
            {
                log.Add(AutomationLogLevel.Warning,
                    $"BAG STOCK SKIPPED {stock.ItemName}: {decision.Reason} No validated live, historical, or existing same-item price was available; stock remains safely in the bags.");
                continue;
            }

            plannedPrices[(stock.ItemId, stock.IsHighQuality)] = target.Value;
            ledger.QueueExistingStock(stock.ItemId, stock.ItemName, stock.ListableQuantity, target.Value,
                stock.TargetStackSize, stock.IsHighQuality, stock.ReservedQuantity,
                ResaleStockPolicy.IsCuratedConsumable(stock.ItemName), stock.StackCount);
            queuedStacks += stock.StackCount;
            log.Add(AutomationLogLevel.Information,
                $"BAG STOCK QUEUED {stock.ItemName}: up to {stock.StackCount} stack(s) of {stock.TargetStackSize} at {target.Value:N0} gil; keeping {stock.ReservedQuantity:N0} in bags.");
        }

        ScheduleNextAttempt();
        if (queuedStacks == 0)
        {
            Status = new(BagListingState.Completed,
                "No resale stacks passed the reserve, market-data, and pricing-floor checks.");
            return;
        }

        configuration.Current.ProcessAllRetainers = true;
        configuration.Save();
        Status = new(BagListingState.QueuePrepared,
            $"Queued up to {queuedStacks} resale stack(s). Starting the all-retainer listing pass.");
        log.Add(AutomationLogLevel.Information, Status.Detail);
        automation.StartBagListingNow();
        // The fill pass changes capacity. Record its result when it completes,
        // so only a subsequent sale triggers an immediate new refill attempt.
        awaitingFillCompletion = true;
    }

    private void TryAutomaticStart()
    {
        if (awaitingFillCompletion && !automation.IsActive)
        {
            awaitingFillCompletion = false;
            lastAttemptFreeSaleSlots = automation.LastKnownFreeSaleSlots;
        }
        if (!IsAutomaticRunDue || procurement.IsActive)
            return;
        PrepareAutomaticRun();
    }

    private IReadOnlyList<ProcurementRule> BuildScanRules() => BuildStockSummary()
        .Where(x => x.StackCount > 0)
        .Select(x => new ProcurementRule
        {
            ItemId = x.ItemId,
            ItemName = x.ItemName,
            TargetStackSize = x.TargetStackSize,
            MaximumSaleSlots = x.StackCount,
            AllowHighQuality = x.IsHighQuality,
            RequireHighQuality = x.IsHighQuality,
        })
        .ToArray();

    private IReadOnlyList<CuratedBagStock> BuildStockSummary()
    {
        return candidates
            .GroupBy(x => new { x.ItemId, x.ItemName, x.IsHighQuality })
            .Select(group =>
            {
                var total = (uint)Math.Min(uint.MaxValue, group.Sum(x => (long)x.Quantity));
                var buyRule = configuration.Current.ProcurementRules.FirstOrDefault(x => x.ItemId == group.Key.ItemId);
                var eligible = ResaleStockPolicy.CanListFromBags(buyRule, group.Key.ItemName, group.Key.IsHighQuality);
                var reserve = ResaleStockPolicy.BagReserve(buyRule, group.Key.ItemName,
                    (uint)configuration.Current.BagListingReservePerItem);
                var fullStacks = ResaleStockPolicy.IsCuratedConsumable(group.Key.ItemName);
                var stackSize = fullStacks ? 99 : Math.Max(1, buyRule?.TargetStackSize ?? 1);
                var ownedSlots = automation.ListedStock.Where(x => x.ItemId == group.Key.ItemId).Sum(x => x.SaleSlots);
                var slotLimit = Math.Max(0, (buyRule?.MaximumSaleSlots ?? 8) - ownedSlots);
                var surplus = total > reserve ? total - reserve : 0;
                var listable = eligible ? Math.Min(surplus, (uint)(slotLimit * stackSize)) : 0;
                if (fullStacks)
                    listable = Math.Min(listable / 99, (uint)group.Sum(x => x.Quantity / 99)) * 99;
                var sourceSlots = fullStacks ? (int)(listable / 99)
                    : (int)group.Sum(x => ((long)x.Quantity + stackSize - 1) / stackSize);
                var stackCount = listable == 0 ? 0 : Math.Min(slotLimit, sourceSlots);
                var pricingRule = configuration.Current.GetEffectiveRule(group.Key.ItemId);
                return new CuratedBagStock(
                    group.Key.ItemId,
                    group.Key.ItemName,
                    total,
                    Math.Min(total, reserve),
                    listable,
                    stackCount,
                    CalculateFloor(pricingRule),
                    plannedPrices.GetValueOrDefault((group.Key.ItemId, group.Key.IsHighQuality)) is var price && price > 0 ? price : null,
                    group.Key.IsHighQuality, stackSize, eligible, group.Count());
            })
            .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static uint CalculateFloor(PricingRule rule)
    {
        if (rule.CostBasis == 0)
            return Math.Max(1, rule.MinimumPrice);
        var marginFloor = decimal.Ceiling(rule.CostBasis * (1m + rule.MinimumMarginPercent / 100m));
        return Math.Max(rule.MinimumPrice,
            (uint)Math.Min(PricingStrategyService.MaximumListingPrice, marginFloor));
    }

    private void PollManualVerification()
    {
        if (pending is null)
            return;
        if (retainerListings.VerifyAutoListing(pending))
        {
            var verified = pending;
            pending = null;
            Status = new(BagListingState.Completed,
                $"Listed {verified.ItemName} x{verified.Quantity:N0} at {verified.UnitPrice:N0} gil.");
            log.Add(AutomationLogLevel.Information,
                $"BAG LIST VERIFIED {verified.ItemName} x{verified.Quantity:N0} at {verified.UnitPrice:N0} gil on {retainerListings.ActiveRetainerName}.");
            RefreshCandidatesOnly();
            return;
        }
        if (this.timeProvider.GetUtcNow() < deadline)
            return;

        var failed = pending;
        pending = null;
        Fail($"Could not verify {failed.ItemName} in the retainer sale slot.",
            $"BAG LIST VERIFICATION FAILED {failed.ItemName}.");
        RefreshCandidatesOnly();
    }

    private void RefreshCandidatesOnly()
    {
        candidates = retainerListings.ReadBagListingCandidates();
        LastBagScanAt = timeProvider.GetUtcNow();
    }

    private void ScheduleNextAttempt()
    {
        lastAttemptFreeSaleSlots = automation.LastKnownFreeSaleSlots;
        nextAutomaticAttempt = timeProvider.GetUtcNow().AddMinutes(5);
    }

    private void Fail(string message, string? logMessage = null)
    {
        Status = new(BagListingState.Failed, message);
        log.Add(AutomationLogLevel.Error, logMessage ?? message);
    }

    private static uint? Median(IEnumerable<uint> source)
    {
        var values = source.Where(x => x > 0).Order().ToArray();
        if (values.Length == 0)
            return null;
        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (uint)(((ulong)values[middle - 1] + values[middle]) / 2);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
    }
}
