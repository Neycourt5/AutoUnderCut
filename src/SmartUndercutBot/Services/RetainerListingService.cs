using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

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
    bool SelectRetainer(int index);
    bool SelectSellItems();
    bool OpenListingContextMenu(int rowIndex);
    bool SelectAdjustPrice();
    bool RequestComparePrices();
    bool TryReadSellerFeePercent(out decimal feePercent);
    void CloseComparePrices();
    void CancelPriceEditor();
    PriceUpdateResult CommitPrice(RetainerListing expected, uint targetPrice);
    bool CloseSellList();
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

public sealed unsafe class RetainerListingService : IRetainerListingService
{
    private const int MaximumRetainerMarketSlots = 20;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;

    public RetainerListingService(IClientState clientState, IObjectTable objectTable, IGameGui gameGui, IDataManager dataManager)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
    }

    public bool IsRetainerListOpen => IsAddonVisible("RetainerList");
    public bool IsRetainerMenuOpen => IsAddonVisible("SelectString");
    public bool IsSellListOpen => IsAddonVisible("RetainerSellList");
    public bool IsContextMenuOpen => IsAddonVisible("ContextMenu");
    public bool IsPriceEditorOpen => IsAddonVisible("RetainerSell");
    public bool IsTalkOpen => IsAddonVisible("Talk");

    public int RetainerCount
    {
        get => AvailableRetainerIndices.Count;
    }

    public IReadOnlyList<int> AvailableRetainerIndices
    {
        get
        {
            var addon = GetAddon("RetainerList");
            if (addon == null || addon->AtkValues == null)
                return [];

            var result = new List<int>(10);
            for (var index = 0; index < 10; index++)
            {
                // RetainerList stores ten AtkValues per visible row beginning at index 3.
                // Offset 0 is the name (null ends the list); offset 8 is the UI's active flag.
                var rowOffset = 3 + (index * 10);
                var activeOffset = rowOffset + 8;
                if (activeOffset >= addon->AtkValuesCount || addon->AtkValues[rowOffset].Type == 0)
                    break;
                var active = addon->AtkValues[activeOffset];
                if (active.Type == AtkValueType.Bool && active.Byte != 0)
                    result.Add(index);
            }
            return result;
        }
    }

    public IReadOnlySet<ulong> OwnedRetainerIds
    {
        get
        {
            var result = new HashSet<ulong>();
            var manager = RetainerManager.Instance();
            if (manager == null)
                return result;
            for (uint i = 0; i < manager->GetRetainerCount(); i++)
            {
                var retainer = manager->GetRetainerBySortedIndex(i);
                if (retainer != null && retainer->RetainerId != 0)
                    result.Add(retainer->RetainerId);
            }
            return result;
        }
    }

    public string ActiveRetainerName
    {
        get
        {
            var manager = RetainerManager.Instance();
            var retainer = manager == null ? null : manager->GetActiveRetainer();
            return retainer == null ? "Unknown retainer" : retainer->NameString;
        }
    }

    public ulong ActiveRetainerId
    {
        get
        {
            var manager = RetainerManager.Instance();
            var retainer = manager == null ? null : manager->GetActiveRetainer();
            return retainer == null ? 0 : retainer->RetainerId;
        }
    }

    public uint PlayerGil
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetGil();
        }
    }

    public uint ActiveRetainerGil
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetRetainerGil();
        }
    }

    public Vector3? GetPlayerPosition() => objectTable.LocalPlayer?.Position;

    public SafetySnapshot CheckSafety(Vector3 sessionPosition)
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer is null)
            return new(false, "Player logged out or local player is unavailable.");
        if (Vector3.DistanceSquared(objectTable.LocalPlayer.Position, sessionPosition) > 0.0025f)
            return new(false, "Player movement detected.");
        return new(true, "Ready");
    }

    public IReadOnlyList<RetainerListing> ReadCurrentListings()
    {
        var results = new List<RetainerListing>(MaximumRetainerMarketSlots);
        var manager = InventoryManager.Instance();
        var retainerManager = RetainerManager.Instance();
        var activeRetainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
        if (manager == null || activeRetainer == null)
            return results;

        var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded || container->Size is < 0 or > MaximumRetainerMarketSlots)
            return results;

        var retainerName = activeRetainer->NameString;
        for (short slot = 0; slot < container->Size; slot++)
        {
            var item = container->GetInventorySlot(slot);
            if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                continue;
            var price = manager->GetRetainerMarketPrice(slot);
            if (price is 0 or > PricingStrategyService.MaximumListingPrice)
                continue;

            var itemName = dataManager.GetExcelSheet<Item>().TryGetRow(item->ItemId, out var row)
                ? row.Name.ToString()
                : $"Item #{item->ItemId}";
            results.Add(new RetainerListing(
                activeRetainer->RetainerId,
                retainerName,
                slot,
                item->ItemId,
                itemName,
                (uint)item->Quantity,
                (uint)price,
                (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
        }

        return results;
    }

    public bool TryReadListing(short slot, out RetainerListing? listing)
    {
        listing = ReadCurrentListings().FirstOrDefault(x => x.Slot == slot);
        return listing is not null;
    }

    public bool TryResolveOpenPriceEditor(uint itemId, IReadOnlySet<short> excludedSlots, out RetainerListing? listing)
    {
        listing = null;
        var addon = gameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->ItemName == null ||
            addon->Quantity == null || addon->AskingPrice == null)
            return false;

        var visibleName = addon->ItemName->NodeText.ToString();
        var visibleQuantity = (uint)Math.Max(0, addon->Quantity->Value);
        var visiblePrice = (uint)Math.Max(0, addon->AskingPrice->Value);
        var visibleHq = visibleName.Contains('\uE03C');

        var candidates = ReadCurrentListings()
            .Where(x => !excludedSlots.Contains(x.Slot))
            .Where(x => visibleName.Contains(x.ItemName, StringComparison.OrdinalIgnoreCase))
            .Where(x => itemId == 0 || x.ItemId == itemId)
            .ToArray();
        if (candidates.Length == 0)
            return false;

        // Prefer every field we can observe. Identical stacks are interchangeable here;
        // excluding prior slots gives each visible row a unique backing market slot.
        listing = candidates.FirstOrDefault(x => x.Quantity == visibleQuantity && x.CurrentPrice == visiblePrice && x.IsHighQuality == visibleHq)
            ?? candidates.FirstOrDefault(x => x.Quantity == visibleQuantity && x.CurrentPrice == visiblePrice)
            ?? candidates.FirstOrDefault(x => x.Quantity == visibleQuantity && x.IsHighQuality == visibleHq)
            ?? candidates.FirstOrDefault(x => x.CurrentPrice == visiblePrice)
            ?? candidates.FirstOrDefault(x => x.IsHighQuality == visibleHq)
            ?? candidates[0];
        return true;
    }

    public bool SelectRetainer(int index)
    {
        var addon = GetAddon("RetainerList");
        if (addon == null || index < 0 || index >= 10 || !AvailableRetainerIndices.Contains(index))
            return false;
        FireCallback(addon, 2, index);
        return true;
    }

    public bool SelectSellItems()
    {
        var addon = GetAddon("SelectString");
        if (addon == null)
            return false;
        // The retainer menu's third entry is "Sell items in your inventory on the market."
        FireCallback(addon, 2);
        return true;
    }

    public bool OpenListingContextMenu(int rowIndex)
    {
        var addon = GetAddon("RetainerSellList");
        if (addon == null || rowIndex < 0 || rowIndex >= MaximumRetainerMarketSlots)
            return false;
        FireCallback(addon, 0, rowIndex, 1);
        return true;
    }

    public bool SelectAdjustPrice()
    {
        var addon = GetAddon("ContextMenu");
        if (addon == null)
            return false;
        // Adjust Price is the first entry for ordinary market listings.
        FireCallback(addon, 0, 0, 0, 0, 0);
        return true;
    }

    public bool RequestComparePrices()
    {
        var addon = gameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return false;
        FireCallback(&addon->AtkUnitBase, 4);
        return true;
    }

    public bool TryReadSellerFeePercent(out decimal feePercent)
    {
        feePercent = 5m;
        var addon = gameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->Tax == null)
            return false;

        var match = Regex.Match(addon->Tax->NodeText.ToString(), @"(?<rate>\d+(?:[\.,]\d+)?)\s*%",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var value = match.Groups["rate"].Value.Replace(',', '.');
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 0 or > 100)
            return false;

        feePercent = parsed;
        return true;
    }

    public void CloseComparePrices()
    {
        var addon = GetAddon("ItemSearchResult");
        if (addon != null)
            addon->Close(true);
    }

    public void CancelPriceEditor()
    {
        var addon = gameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
        if (addon == null)
            return;
        FireCallback(&addon->AtkUnitBase, 1);
        addon->AtkUnitBase.Close(true);
    }

    public PriceUpdateResult CommitPrice(RetainerListing expected, uint targetPrice)
    {
        if (targetPrice is 0 or > PricingStrategyService.MaximumListingPrice)
            return new(false, "Target price is outside valid game bounds.");

        var addon = gameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->AskingPrice == null)
            return new(false, "The Adjust Price window is no longer available.");
        if (!TryReadListing(expected.Slot, out var current) || current is null ||
            current.RetainerId != expected.RetainerId || current.ItemId != expected.ItemId ||
            current.Quantity != expected.Quantity || current.CurrentPrice != expected.CurrentPrice ||
            current.IsHighQuality != expected.IsHighQuality)
            return new(false, "The underlying listing changed after evaluation; update was cancelled.");

        // SetValue is the same safe component path used by established repricers. It
        // overwrites any Penny Pincher prefill and dispatches the numeric-input change.
        addon->AskingPrice->SetValue((int)targetPrice);

        // Some UI revisions do not propagate SetValue into RetainerSell's AtkValues.
        // Callback 2 is the addon's native asking-price update path, so use it as a
        // fallback and verify both representations before the Confirm callback.
        if (addon->AskingPrice->Value != targetPrice || addon->AtkUnitBase.AtkValues == null ||
            addon->AtkUnitBase.AtkValuesCount <= 5 || addon->AtkUnitBase.AtkValues[5].Int != targetPrice)
            FireCallback(&addon->AtkUnitBase, 2, (int)targetPrice);
        if (addon->AskingPrice->Value != targetPrice || addon->AtkUnitBase.AtkValues == null ||
            addon->AtkUnitBase.AtkValuesCount <= 5 || addon->AtkUnitBase.AtkValues[5].Int != targetPrice)
            return new(false, "The Adjust Price input did not accept the target value.");
        if (addon->Confirm == null || !addon->Confirm->IsEnabled)
            return new(false, "The Adjust Price confirmation button is unavailable.");

        // Callback 0 is RetainerSell's native Confirm action. Do not synthesize a raw
        // ReceiveEvent here: the event object requires game-owned data and caused an
        // access violation in AddonRetainerSell.ReceiveEvent on 2026-06-19.
        FireCallback(&addon->AtkUnitBase, 0);
        addon->AtkUnitBase.Close(true);
        return new(true, "Set the numeric price and submitted the Adjust Price confirmation callback.");
    }

    public bool CloseSellList()
    {
        var addon = GetAddon("RetainerSellList");
        if (addon == null)
            return false;
        addon->Close(true);
        return true;
    }

    public bool CloseRetainerMenu()
    {
        var addon = GetAddon("SelectString");
        if (addon == null)
            return false;
        addon->Close(true);
        return true;
    }

    public bool AdvanceTalk()
    {
        var addon = GetAddon("Talk");
        if (addon == null)
            return false;

        var evt = stackalloc AtkEvent[1]
        {
            new()
            {
                Listener = (AtkEventListener*)addon,
                Target = &AtkStage.Instance()->AtkEventTarget,
                State = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
            },
        };
        var data = stackalloc AtkEventData[1];
        *data = default;
        addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
        return true;
    }

    public bool TryAutoListPurchase(ProcurementLedger ledger, out PendingAutoListing? pending)
    {
        pending = null;
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;
        var market = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null || !market->IsLoaded || market->Size is <= 0 or > MaximumRetainerMarketSlots)
            return false;

        short destinationSlot = -1;
        for (short slot = 0; slot < market->Size; slot++)
        {
            var item = market->GetInventorySlot(slot);
            if (item != null && item->ItemId == 0)
            {
                destinationSlot = slot;
                break;
            }
        }
        if (destinationSlot < 0)
            return false;

        var inventoryTypes = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };
        foreach (var type in inventoryTypes)
        {
            var inventory = manager->GetInventoryContainer(type);
            if (inventory == null || !inventory->IsLoaded)
                continue;
            for (ushort sourceSlot = 0; sourceSlot < inventory->Size; sourceSlot++)
            {
                var item = inventory->GetInventorySlot(sourceSlot);
                var isHighQuality = item != null &&
                                    (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (item == null || item->ItemId == 0 || item->Quantity <= 0 ||
                    !ledger.TryGetPending(item->ItemId, isHighQuality, out var entry) || entry is null)
                    continue;
                var quantity = Math.Min((uint)item->Quantity, Math.Min(entry.PendingQuantity, (uint)entry.TargetStackSize));
                if (quantity == 0 || entry.TargetSalePrice == 0)
                    continue;

                manager->MoveToRetainerMarket(type, sourceSlot, InventoryType.RetainerMarket,
                    (ushort)destinationSlot, quantity, entry.TargetSalePrice);
                pending = new(entry.ItemId, entry.ItemName, quantity, entry.TargetSalePrice, destinationSlot,
                    entry.IsHighQuality);
                return true;
            }
        }
        return false;
    }

    public IReadOnlyList<BagListingCandidate> ReadBagListingCandidates()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return [];

        var results = new List<BagListingCandidate>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        foreach (var type in PlayerInventoryTypes)
        {
            var inventory = manager->GetInventoryContainer(type);
            if (inventory == null || !inventory->IsLoaded)
                continue;
            for (ushort slot = 0; slot < inventory->Size; slot++)
            {
                var item = inventory->GetInventorySlot(slot);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0 ||
                    !itemSheet.TryGetRow(item->ItemId, out var row) || row.StackSize == 0 ||
                    row.IsUntradable || row.ItemSearchCategory.RowId == 0)
                    continue;
                results.Add(new(
                    type,
                    slot,
                    item->ItemId,
                    row.Name.ToString(),
                    (uint)item->Quantity,
                    (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                    row.StackSize));
            }
        }
        return results
            .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.IsHighQuality)
            .ThenBy(x => x.SourceType)
            .ThenBy(x => x.SourceSlot)
            .ToArray();
    }

    public bool TryListBagItem(
        BagListingCandidate candidate,
        uint quantity,
        uint unitPrice,
        out PendingAutoListing? pending,
        out string message)
    {
        pending = null;
        message = string.Empty;
        if (!IsSellListOpen)
        {
            message = "Open a retainer's market sell list first.";
            return false;
        }
        if (quantity == 0 || quantity > candidate.Quantity || quantity > candidate.StackSize)
        {
            message = "Quantity is outside the selected stack's valid range.";
            return false;
        }
        if (unitPrice is 0 or > PricingStrategyService.MaximumListingPrice)
        {
            message = "Unit price is outside the market-board range.";
            return false;
        }

        var manager = InventoryManager.Instance();
        var source = manager == null ? null : manager->GetInventoryContainer(candidate.SourceType);
        var market = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null || source == null || !source->IsLoaded || market == null || !market->IsLoaded ||
            candidate.SourceSlot >= source->Size)
        {
            message = "The bag or retainer inventory is not currently available.";
            return false;
        }
        var item = source->GetInventorySlot(candidate.SourceSlot);
        if (item == null || item->ItemId != candidate.ItemId || item->Quantity < quantity ||
            ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != candidate.IsHighQuality)
        {
            message = "The selected bag stack changed; refresh and select it again.";
            return false;
        }

        short destinationSlot = -1;
        for (short slot = 0; slot < market->Size; slot++)
        {
            var destination = market->GetInventorySlot(slot);
            if (destination != null && destination->ItemId == 0)
            {
                destinationSlot = slot;
                break;
            }
        }
        if (destinationSlot < 0)
        {
            message = "This retainer has no free market sale slots.";
            return false;
        }

        manager->MoveToRetainerMarket(candidate.SourceType, candidate.SourceSlot, InventoryType.RetainerMarket,
            (ushort)destinationSlot, quantity, unitPrice);
        pending = new(candidate.ItemId, candidate.ItemName, quantity, unitPrice, destinationSlot,
            candidate.IsHighQuality);
        message = $"Submitted {candidate.ItemName} x{quantity:N0} at {unitPrice:N0} gil.";
        return true;
    }

    public bool VerifyAutoListing(PendingAutoListing pending)
    {
        var manager = InventoryManager.Instance();
        var market = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null || market == null || !market->IsLoaded || pending.MarketSlot < 0 || pending.MarketSlot >= market->Size)
            return false;
        var item = market->GetInventorySlot(pending.MarketSlot);
        return item != null && item->ItemId == pending.ItemId && item->Quantity == pending.Quantity &&
               ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) == pending.IsHighQuality &&
               manager->GetRetainerMarketPrice(pending.MarketSlot) == pending.UnitPrice;
    }

    private AtkUnitBase* GetAddon(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible ? addon : null;
    }

    private bool IsAddonVisible(string name) => GetAddon(name) != null;

    private static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static void FireCallback(AtkUnitBase* addon, params int[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i].Type = AtkValueType.Int;
            atkValues[i].Int = values[i];
        }
        addon->FireCallback((uint)values.Length, atkValues, true);
    }
}
