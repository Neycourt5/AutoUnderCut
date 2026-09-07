using System.Numerics;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Services;

namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public enum InventoryType { Inventory1 }
}

namespace SmartUndercutBot.Core.Tests.Automation
{
    // Native contract is linked from production; unsupported fake operations fail
    // explicitly so a new controller path cannot silently pass a test.
    public class FakeRetainerService : IRetainerListingService
    {
        public virtual bool IsRetainerListOpen => false;
        public virtual bool IsRetainerMenuOpen => false;
        public virtual bool IsSellListOpen => false;
        public virtual bool IsContextMenuOpen => false;
        public virtual bool IsPriceEditorOpen => false;
        public virtual bool IsBankOpen => false;
        public virtual bool IsTalkOpen => false;
        public int RetainerCount => AvailableRetainerIndices.Count;
        public virtual IReadOnlyList<int> AvailableRetainerIndices => [0];
        public virtual IReadOnlySet<ulong> OwnedRetainerIds => new HashSet<ulong> { 1 };
        public virtual ulong ActiveRetainerId => 1;
        public virtual string ActiveRetainerName => "Test Retainer";
        public virtual uint PlayerGil => 0;
        public virtual uint ActiveRetainerGil => 0;
        public virtual SafetySnapshot CheckSafety(Vector3 position) => new(true, string.Empty);
        public virtual IReadOnlyList<RetainerListing> ReadCurrentListings() => [];
        public virtual bool SelectRetainer(int index) => throw new NotSupportedException();
        public virtual bool SelectSellItems() => throw new NotSupportedException();
        public virtual bool CloseSellList() => throw new NotSupportedException();
        public virtual bool CloseRetainerMenu() => throw new NotSupportedException();
        public virtual bool AdvanceTalk() => throw new NotSupportedException();
        public virtual void CloseComparePrices() { }
        public virtual void CloseContextMenu() { }
        public virtual void CancelPriceEditor() { }
        public virtual void CancelBankDialog() { }
        public virtual Vector3? GetPlayerPosition() => Vector3.Zero;
        public virtual bool TryAutoListPurchase(ProcurementLedger ledger, out PendingAutoListing? pending)
        { pending = null; return false; }
        public virtual bool VerifyAutoListing(PendingAutoListing pending) => throw new NotSupportedException();
        public virtual bool TryReadListing(short slot, out RetainerListing? listing) => throw new NotSupportedException();
        public virtual bool TryResolveOpenPriceEditor(uint id, IReadOnlySet<short> slots, out RetainerListing? listing) => throw new NotSupportedException();
        public virtual bool IsOpenPriceEditorFor(RetainerListing listing, bool requirePriceMatch) => throw new NotSupportedException();
        public virtual bool OpenListingContextMenu(int index) => throw new NotSupportedException();
        public virtual bool SelectAdjustPrice() => throw new NotSupportedException();
        public virtual bool RequestComparePrices() => throw new NotSupportedException();
        public bool TryReadSellerFeePercent(out decimal feePercent) { feePercent = 5m; return true; }
        public virtual PriceUpdateResult CommitPrice(RetainerListing expected, uint price) => throw new NotSupportedException();
        public bool SelectEntrustGil(out string message) => throw new NotSupportedException();
        public bool SetWithdrawAllRetainerGil(out uint amount, out string message) => throw new NotSupportedException();
        public bool ConfirmGilWithdrawal() => throw new NotSupportedException();
        public virtual IReadOnlyList<BagListingCandidate> ReadBagListingCandidates() => [];
        public bool TryListBagItem(BagListingCandidate candidate, uint quantity, uint price,
            out PendingAutoListing? pending, out string message) => throw new NotSupportedException();
    }
}
