using System.Numerics;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public enum AutomationState
{
    Idle,
    WaitingForStableInterface,
    ReadingListings,
    RequestingMarketData,
    EvaluatingPrice,
    WaitingBeforeCommit,
    CommittingPrice,
    WaitingAfterCommit,
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
    DateTimeOffset? NextActionAt);

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
    private int currentIndex;
    private int updatesSubmitted;
    private bool handledCurrentOpenInterface;
    private string detail = "Waiting for a retainer sell list.";

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
        State is AutomationState.WaitingForStableInterface or AutomationState.WaitingBeforeCommit or AutomationState.WaitingAfterCommit
            ? nextActionAt
            : null);

    public IReadOnlyList<AutomationQueueEntry> QueueSnapshot() => queue.ToArray();

    public void StartNow()
    {
        if (!retainerListings.IsSellListOpen)
        {
            Halt("Cannot start: open a retainer's sell-list interface first.");
            return;
        }

        BeginSession();
    }

    public void Halt(string reason = "Stopped by user.")
    {
        sessionCancellation?.Cancel();
        marketTask = null;
        currentMarket = null;
        currentDecision = null;
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
        var sellListOpen = retainerListings.IsSellListOpen;
        if (!sellListOpen)
        {
            handledCurrentOpenInterface = false;
            if (State is not AutomationState.Idle and not AutomationState.Halted and not AutomationState.Faulted)
                Halt("Retainer sell-list interface closed.");
            if (State is AutomationState.Halted or AutomationState.Completed or AutomationState.Faulted)
            {
                State = AutomationState.Idle;
                detail = "Waiting for a retainer sell list.";
            }
            return;
        }

        if (!handledCurrentOpenInterface)
        {
            handledCurrentOpenInterface = true;
            RetainerInterfaceOpened?.Invoke();
            if (configuration.Current.AutomationEnabled)
                BeginSession();
        }

        if (State is AutomationState.Idle or AutomationState.Completed or AutomationState.Halted or AutomationState.Faulted)
            return;

        var safety = retainerListings.CheckSafety(sessionPosition);
        if (!safety.IsSafe)
        {
            Halt(safety.Reason);
            return;
        }

        switch (State)
        {
            case AutomationState.WaitingForStableInterface:
                if (DelayElapsed()) Transition(AutomationState.ReadingListings, "Reading current retainer listings.");
                break;
            case AutomationState.ReadingListings:
                ReadListings();
                break;
            case AutomationState.RequestingMarketData:
                PollMarketRequest();
                break;
            case AutomationState.EvaluatingPrice:
                EvaluateCurrentListing();
                break;
            case AutomationState.WaitingBeforeCommit:
                if (DelayElapsed())
                {
                    if (!configuration.Current.AllowAutomaticWrites)
                    {
                        var entry = queue[currentIndex];
                        ReplaceCurrent(entry with { Status = "Disarmed before commit" });
                        log.Add(AutomationLogLevel.Warning, $"{entry.Listing.ItemName}: write was disarmed before commit; skipped.");
                        Schedule(AutomationState.WaitingAfterCommit, "Write disarmed; waiting before the next listing.");
                    }
                    else
                    {
                        CommitCurrentPrice();
                    }
                }
                break;
            case AutomationState.WaitingAfterCommit:
                if (DelayElapsed()) VerifyCommitAndMoveNext();
                break;
        }
    }

    private void BeginSession()
    {
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        sessionCancellation = new CancellationTokenSource();
        queue.Clear();
        currentIndex = 0;
        updatesSubmitted = 0;
        currentMarket = null;
        currentDecision = null;
        marketTask = null;
        sessionPosition = retainerListings.GetPlayerPosition() ?? Vector3.Zero;
        handledCurrentOpenInterface = true;
        Schedule(AutomationState.WaitingForStableInterface, "Waiting for the retainer interface to stabilize.");
        log.Add(AutomationLogLevel.Information,
            configuration.Current.AllowAutomaticWrites
                ? "Automation session started with autonomous writes armed."
                : "Automation session started in dry-run mode; no prices will be submitted.");
    }

    private void ReadListings()
    {
        var listings = retainerListings.ReadCurrentListings();
        queue.Clear();
        queue.AddRange(listings.Select((listing, index) => new AutomationQueueEntry(index, listing, "Queued")));
        log.Add(AutomationLogLevel.Information, $"Queued {queue.Count} retainer listing(s).");
        if (queue.Count == 0)
        {
            Complete("No valid listings were found.");
            return;
        }
        RequestCurrentMarket();
    }

    private void RequestCurrentMarket()
    {
        if (currentIndex >= queue.Count)
        {
            Complete($"Processed {queue.Count} listing(s); submitted {updatesSubmitted} update(s).");
            return;
        }

        var entry = queue[currentIndex];
        ReplaceCurrent(entry with { Status = "Loading market data" });
        currentMarket = null;
        currentDecision = null;
        marketTask = marketData.GetSnapshotAsync(entry.Listing.ItemId, sessionCancellation!.Token);
        Transition(AutomationState.RequestingMarketData, $"Loading market data for {entry.Listing.ItemName}.");
    }

    private void PollMarketRequest()
    {
        if (marketTask is null || !marketTask.IsCompleted)
            return;

        if (marketTask.IsCanceled)
        {
            Halt("Market request was cancelled.");
            return;
        }
        if (marketTask.IsFaulted)
        {
            var message = marketTask.Exception?.GetBaseException().Message ?? "Unknown market-data error.";
            log.Add(AutomationLogLevel.Error, $"{queue[currentIndex].Listing.ItemName}: {message}");
            ReplaceCurrent(queue[currentIndex] with { Status = "Market data failed" });
            Schedule(AutomationState.WaitingAfterCommit, "Waiting before the next market request.");
            return;
        }

        currentMarket = marketTask.Result;
        var age = DateTimeOffset.UtcNow - currentMarket.CapturedAt;
        if (age > TimeSpan.FromSeconds(configuration.Current.MaximumMarketDataAgeSeconds))
        {
            log.Add(AutomationLogLevel.Warning, $"{queue[currentIndex].Listing.ItemName}: rejected stale market data ({age.TotalSeconds:F0}s old).");
            ReplaceCurrent(queue[currentIndex] with { Status = "Stale data skipped" });
            Schedule(AutomationState.WaitingAfterCommit, "Waiting before the next market request.");
            return;
        }
        Transition(AutomationState.EvaluatingPrice, $"Evaluating {queue[currentIndex].Listing.ItemName}.");
    }

    private void EvaluateCurrentListing()
    {
        var entry = queue[currentIndex];
        currentDecision = pricing.Evaluate(new PricingContext(
            entry.Listing,
            currentMarket!,
            configuration.Current.GetEffectiveRule(entry.Listing.ItemId)));
        ReplaceCurrent(entry with { Status = currentDecision.Kind.ToString(), Decision = currentDecision });

        if (!currentDecision.ShouldUpdate)
        {
            log.Add(AutomationLogLevel.Information,
                $"{entry.Listing.ItemName}: skipped at {entry.Listing.CurrentPrice:N0} gil — {currentDecision.Reason}");
            Schedule(AutomationState.WaitingAfterCommit, "Waiting before the next listing.");
            return;
        }

        if (!configuration.Current.AllowAutomaticWrites)
        {
            log.Add(AutomationLogLevel.Information,
                $"DRY RUN {entry.Listing.ItemName}: {entry.Listing.CurrentPrice:N0} → {currentDecision.TargetPrice:N0} gil.");
            ReplaceCurrent(queue[currentIndex] with { Status = "Dry-run update" });
            Schedule(AutomationState.WaitingAfterCommit, "Dry-run decision recorded.");
            return;
        }

        if (updatesSubmitted >= configuration.Current.MaximumUpdatesPerSession)
        {
            Complete($"Stopped at the configured session limit of {updatesSubmitted} update(s).");
            return;
        }

        Schedule(AutomationState.WaitingBeforeCommit,
            $"Waiting before updating {entry.Listing.ItemName} to {currentDecision.TargetPrice:N0} gil.");
    }

    private void CommitCurrentPrice()
    {
        Transition(AutomationState.CommittingPrice, $"Submitting price for {queue[currentIndex].Listing.ItemName}.");
        var entry = queue[currentIndex];
        var target = currentDecision!.TargetPrice!.Value;
        var result = retainerListings.UpdatePrice(entry.Listing, target);
        if (!result.Succeeded)
        {
            ReplaceCurrent(entry with { Status = "Commit rejected" });
            log.Add(AutomationLogLevel.Error, $"{entry.Listing.ItemName}: price update rejected — {result.Message}");
            Halt(result.Message);
            return;
        }

        updatesSubmitted++;
        ReplaceCurrent(entry with { Status = $"Submitted {target:N0} gil" });
        log.Add(AutomationLogLevel.Information,
            $"UPDATED {entry.Listing.ItemName} (slot {entry.Listing.Slot}): {entry.Listing.CurrentPrice:N0} → {target:N0} gil. {result.Message}");
        Schedule(AutomationState.WaitingAfterCommit, "Waiting for the client/server update before continuing.");
    }

    private void MoveNext()
    {
        currentIndex++;
        marketTask = null;
        currentMarket = null;
        currentDecision = null;
        RequestCurrentMarket();
    }

    private void VerifyCommitAndMoveNext()
    {
        var entry = queue[currentIndex];
        if (configuration.Current.AllowAutomaticWrites && currentDecision?.ShouldUpdate == true &&
            entry.Status.StartsWith("Submitted", StringComparison.Ordinal))
        {
            var target = currentDecision.TargetPrice!.Value;
            if (!retainerListings.TryReadListing(entry.Listing.Slot, out var current) || current is null ||
                current.RetainerId != entry.Listing.RetainerId || current.ItemId != entry.Listing.ItemId ||
                current.CurrentPrice != target)
            {
                Halt($"Could not verify the submitted {target:N0} gil price for {entry.Listing.ItemName}.");
                return;
            }
            ReplaceCurrent(entry with { Status = $"Verified {target:N0} gil" });
            log.Add(AutomationLogLevel.Information,
                $"VERIFIED {entry.Listing.ItemName} (slot {entry.Listing.Slot}) at {target:N0} gil.");
        }

        MoveNext();
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

    private bool DelayElapsed() => DateTimeOffset.UtcNow >= nextActionAt;

    private void Transition(AutomationState state, string message)
    {
        State = state;
        detail = message;
    }

    private void ReplaceCurrent(AutomationQueueEntry entry) => queue[currentIndex] = entry;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
    }
}
