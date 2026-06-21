using Dalamud.Plugin.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public enum BagListingState
{
    Idle,
    WaitingForVerification,
    Completed,
    Failed,
}

public sealed record BagListingStatus(BagListingState State, string Detail);

public sealed class BagListingController : IDisposable
{
    private readonly IFramework framework;
    private readonly IRetainerListingService retainerListings;
    private readonly AutomationLog log;
    private IReadOnlyList<BagListingCandidate> candidates = [];
    private PendingAutoListing? pending;
    private DateTimeOffset deadline;

    public BagListingController(
        IFramework framework,
        IRetainerListingService retainerListings,
        AutomationLog log)
    {
        this.framework = framework;
        this.retainerListings = retainerListings;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public BagListingStatus Status { get; private set; } = new(BagListingState.Idle,
        "Open a retainer sell list, select a bag stack, and enter its unit price.");
    public IReadOnlyList<BagListingCandidate> Candidates => candidates;
    public bool IsBusy => State == BagListingState.WaitingForVerification;
    public bool IsRetainerSellListOpen => retainerListings.IsSellListOpen;
    private BagListingState State => Status.State;

    public void Refresh()
    {
        if (IsBusy)
            return;
        candidates = retainerListings.ReadBagListingCandidates();
        Status = new(BagListingState.Idle, $"Found {candidates.Count} marketable bag stack(s).");
    }

    public bool List(BagListingCandidate candidate, uint quantity, uint unitPrice)
    {
        if (IsBusy)
            return false;
        if (!retainerListings.TryListBagItem(candidate, quantity, unitPrice, out pending, out var message) ||
            pending is null)
        {
            Status = new(BagListingState.Failed, message);
            log.Add(AutomationLogLevel.Error, $"BAG LIST FAILED {candidate.ItemName}: {message}");
            return false;
        }

        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        Status = new(BagListingState.WaitingForVerification, message + " Waiting for retainer verification.");
        log.Add(AutomationLogLevel.Information, $"BAG LIST SUBMITTED {message}");
        return true;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (State != BagListingState.WaitingForVerification || pending is null)
            return;
        if (retainerListings.VerifyAutoListing(pending))
        {
            var verified = pending;
            pending = null;
            Status = new(BagListingState.Completed,
                $"Listed {verified.ItemName} x{verified.Quantity:N0} at {verified.UnitPrice:N0} gil.");
            log.Add(AutomationLogLevel.Information,
                $"BAG LIST VERIFIED {verified.ItemName} x{verified.Quantity:N0} at {verified.UnitPrice:N0} gil on {retainerListings.ActiveRetainerName}.");
            candidates = retainerListings.ReadBagListingCandidates();
            return;
        }
        if (DateTimeOffset.UtcNow < deadline)
            return;

        var failed = pending;
        pending = null;
        Status = new(BagListingState.Failed,
            $"Could not verify {failed.ItemName} in the retainer sale slot.");
        log.Add(AutomationLogLevel.Error, $"BAG LIST VERIFICATION FAILED {failed.ItemName}.");
        candidates = retainerListings.ReadBagListingCandidates();
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
