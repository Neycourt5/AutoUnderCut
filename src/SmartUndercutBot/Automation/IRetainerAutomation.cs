using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Automation;

public interface IRetainerAutomation
{
    int? LastKnownFreeSaleSlots { get; }
    IReadOnlyList<StockExposure> ListedStock { get; }
    // Home-world prices already read while repricing, so shopping need not sweep
    // the same items again. Empty by default for doubles that do not track them.
    IReadOnlyDictionary<uint, (DateTimeOffset At, IReadOnlyList<MarketListing> Listings)> ObservedHomePrices
        => new Dictionary<uint, (DateTimeOffset, IReadOnlyList<MarketListing>)>();
    bool IsActive { get; }
    bool RequiresManualRestart { get; }
    void StartNow();
    void Halt(string reason);
}
