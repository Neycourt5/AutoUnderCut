using System.Numerics;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public sealed record LivePurchaseListing(
    int Index,
    uint ItemId,
    ulong ListingId,
    ulong RetainerId,
    uint PricePerUnit,
    uint Quantity,
    bool IsHighQuality,
    uint TotalTax);

public interface IMarketPurchaseService
{
    string? SearchStatus => null;
    uint PurchaseError => 0;
    bool IsMarketBoardOpen { get; }
    uint FreeInventorySlots { get; }
    uint Gil { get; }
    int GetInventoryCount(uint itemId, bool highQuality);
    Vector3? FindNearest(string objectName);
    float DistanceTo(Vector3 position);
    bool InteractNearest(string objectName, float maximumDistance = 5f);
    bool RequestListings(uint itemId);
    bool AreListingsReady(uint itemId);
    void ResetListingRequest();
    IReadOnlyList<LivePurchaseListing> ReadLiveListings(uint itemId);

    /// <summary>
    /// Whether the listings already in hand for this item may be reused for another
    /// purchase instead of repeating the whole search. Bounded by how long ago the
    /// response was actually observed on the network, not by when it was last read.
    /// </summary>
    bool IsListingSessionReusable(uint itemId, TimeSpan maximumAge);

    /// <summary>
    /// When the current listings for this item were observed to be complete. Null
    /// when no complete response is held, so callers cannot restamp cached rows as
    /// though they had just arrived.
    /// </summary>
    DateTimeOffset? ListingsObservedAt(uint itemId);

    /// <summary>
    /// Resolves the listing to buy. Excluded listing IDs are removed before any
    /// candidate is chosen - filtering after selection lets an already-purchased row
    /// that is still visible keep winning, and the fallback would never reach the
    /// next eligible stack.
    /// </summary>
    bool TrySelectLiveListing(
        ProcurementOrder expected,
        IReadOnlySet<ulong> excludedRetainerIds,
        IReadOnlySet<ulong> excludedListingIds,
        out LivePurchaseListing? listing);
    bool SubmitPurchase(LivePurchaseListing listing);
    // Optional UI purchase path only. Direct packet submissions do not require a prompt.
    bool TryConfirmPurchase(string itemName) => false;
    void CloseMarketBoard();
    void CloseRetainerList();
}
