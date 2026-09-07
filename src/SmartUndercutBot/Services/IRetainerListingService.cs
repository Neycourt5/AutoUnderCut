using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public sealed record SafetySnapshot(bool IsSafe, string Reason);
public sealed record PriceUpdateResult(bool Succeeded, string Message);
public sealed record PendingAutoListing(
    uint ItemId,
    string ItemName,
    uint Quantity,
    uint UnitPrice,
    short MarketSlot,
    bool IsHighQuality);
public sealed record BagListingCandidate(
    InventoryType SourceType,
    ushort SourceSlot,
    uint ItemId,
    string ItemName,
    uint Quantity,
    bool IsHighQuality,
    uint StackSize);

public interface IRetainerListingService
{
    bool IsRetainerListOpen { get; }
    bool IsRetainerMenuOpen { get; }
    bool IsSellListOpen { get; }
    bool IsContextMenuOpen { get; }
    bool IsPriceEditorOpen { get; }
    bool IsBankOpen { get; }
    bool IsTalkOpen { get; }
    int RetainerCount { get; }
    IReadOnlyList<int> AvailableRetainerIndices { get; }
    IReadOnlySet<ulong> OwnedRetainerIds { get; }
    ulong ActiveRetainerId { get; }
    string ActiveRetainerName { get; }
    uint PlayerGil { get; }
    uint ActiveRetainerGil { get; }
    SafetySnapshot CheckSafety(Vector3 sessionPosition);
    IReadOnlyList<RetainerListing> ReadCurrentListings();
    bool TryReadListing(short slot, out RetainerListing? listing);
    bool TryResolveOpenPriceEditor(uint itemId, IReadOnlySet<short> excludedSlots, out RetainerListing? listing);
    bool IsOpenPriceEditorFor(RetainerListing expected, bool requirePriceMatch);
    bool SelectRetainer(int index);
    bool SelectSellItems();
    bool OpenListingContextMenu(int rowIndex);
    bool SelectAdjustPrice();
    bool RequestComparePrices();
    bool TryReadSellerFeePercent(out decimal feePercent);
    void CloseComparePrices();
    void CloseContextMenu();
    void CancelPriceEditor();
    PriceUpdateResult CommitPrice(RetainerListing expected, uint targetPrice);
    bool CloseSellList();
    bool SelectEntrustGil(out string message);
    bool SetWithdrawAllRetainerGil(out uint amount, out string message);
    bool ConfirmGilWithdrawal();
    void CancelBankDialog();
    bool CloseRetainerMenu();
    bool AdvanceTalk();
    bool TryAutoListPurchase(ProcurementLedger ledger, out PendingAutoListing? pending);
    IReadOnlyList<BagListingCandidate> ReadBagListingCandidates();
    bool TryListBagItem(
        BagListingCandidate candidate,
        uint quantity,
        uint unitPrice,
        out PendingAutoListing? pending,
        out string message);
    bool VerifyAutoListing(PendingAutoListing pending);
    Vector3? GetPlayerPosition();
}

