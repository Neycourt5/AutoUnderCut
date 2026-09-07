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
    bool TrySelectLiveListing(
        ProcurementOrder expected,
        IReadOnlySet<ulong> excludedRetainerIds,
        out LivePurchaseListing? listing);
    bool SubmitPurchase(LivePurchaseListing listing);
    void CloseMarketBoard();
    void CloseRetainerList();
}
