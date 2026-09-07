using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public interface IMarketDataService
{
    Task<MarketSnapshot> GetSnapshotAsync(uint itemId, CancellationToken cancellationToken);
    void ClearCache();
    // What the last request actually observed, so a timeout can say whether any
    // market packet arrived at all rather than just "timed out".
    string LastRequestSummary => string.Empty;
}

