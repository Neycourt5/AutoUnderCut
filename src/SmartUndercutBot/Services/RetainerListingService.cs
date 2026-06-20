using System.Numerics;
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

public interface IRetainerListingService
{
    bool IsRetainerListOpen { get; }
    bool IsRetainerMenuOpen { get; }
    bool IsSellListOpen { get; }
    bool IsContextMenuOpen { get; }
    bool IsPriceEditorOpen { get; }
    bool IsTalkOpen { get; }
    int RetainerCount { get; }
    IReadOnlySet<ulong> OwnedRetainerIds { get; }
    string ActiveRetainerName { get; }
    SafetySnapshot CheckSafety(Vector3 sessionPosition);
    IReadOnlyList<RetainerListing> ReadCurrentListings();
    bool TryReadListing(short slot, out RetainerListing? listing);
    bool TryResolveOpenPriceEditor(IReadOnlySet<short> excludedSlots, out RetainerListing? listing);
    bool SelectRetainer(int index);
    bool SelectSellItems();
    bool OpenListingContextMenu(int rowIndex);
    bool SelectAdjustPrice();
    bool RequestComparePrices();
    void CloseComparePrices();
    void CancelPriceEditor();
    PriceUpdateResult CommitPrice(RetainerListing expected, uint targetPrice);
    bool CloseSellList();
    bool CloseRetainerMenu();
    bool AdvanceTalk();
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
        get
        {
            var manager = RetainerManager.Instance();
            return manager == null ? 0 : (int)Math.Min(manager->GetRetainerCount(), 10u);
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

    public bool TryResolveOpenPriceEditor(IReadOnlySet<short> excludedSlots, out RetainerListing? listing)
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
            .Where(x => x.Quantity == visibleQuantity)
            .ToArray();
        if (candidates.Length == 0)
            return false;

        // Prefer every field we can observe. Identical stacks are interchangeable here;
        // excluding prior slots gives each visible row a unique backing market slot.
        listing = candidates.FirstOrDefault(x => x.CurrentPrice == visiblePrice && x.IsHighQuality == visibleHq)
            ?? candidates.FirstOrDefault(x => x.CurrentPrice == visiblePrice)
            ?? candidates.FirstOrDefault(x => x.IsHighQuality == visibleHq)
            ?? candidates[0];
        return true;
    }

    public bool SelectRetainer(int index)
    {
        var addon = GetAddon("RetainerList");
        if (addon == null || index < 0 || index >= RetainerCount)
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

        // Callback 2 is the Adjust Price addon's own numeric-input update path. It also
        // overwrites any Penny Pincher prefill before we press the real Confirm button.
        FireCallback(&addon->AtkUnitBase, 2, (int)targetPrice);
        if (addon->AskingPrice->Value != targetPrice)
            return new(false, "The Adjust Price input did not accept the target value.");
        if (addon->Confirm == null || !addon->Confirm->IsEnabled)
            return new(false, "The Adjust Price confirmation button is unavailable.");

        var buttonNode = addon->Confirm->AtkComponentBase.OwnerNode->AtkResNode;
        var clickEvent = (AtkEvent*)buttonNode.AtkEventManager.Event;
        if (clickEvent == null)
            return new(false, "The Adjust Price confirmation event is unavailable.");

        addon->AtkUnitBase.ReceiveEvent(
            clickEvent->State.EventType,
            (int)clickEvent->Param,
            (AtkEvent*)buttonNode.AtkEventManager.Event);
        return new(true, "Pressed the Adjust Price confirmation button.");
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

    private AtkUnitBase* GetAddon(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible ? addon : null;
    }

    private bool IsAddonVisible(string name) => GetAddon(name) != null;

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
