using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
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

public sealed unsafe class MarketPurchaseService : IMarketPurchaseService
{
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private uint visibleSearchItemId;
    private bool visibleSearchResultSelected;
    private DateTimeOffset nextVisibleSelectionAt;

    public MarketPurchaseService(IObjectTable objectTable, IGameGui gameGui, IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
    }

    public bool IsMarketBoardOpen => IsAddonVisible("ItemSearch");
    public uint FreeInventorySlots
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetEmptySlotsInBag();
        }
    }
    public uint Gil
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetGil();
        }
    }

    public int GetInventoryCount(uint itemId, bool highQuality)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(itemId, highQuality, false, false, 0);
    }

    public Vector3? FindNearest(string objectName) => objectTable
        .Where(x => string.Equals(x.Name.TextValue, objectName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => Vector3.DistanceSquared(x.Position, objectTable.LocalPlayer?.Position ?? Vector3.Zero))
        .Select(x => (Vector3?)x.Position)
        .FirstOrDefault();

    public float DistanceTo(Vector3 position) => objectTable.LocalPlayer is { } player
        ? Vector3.Distance(player.Position, position)
        : float.MaxValue;

    public bool InteractNearest(string objectName, float maximumDistance = 5f)
    {
        var player = objectTable.LocalPlayer;
        var target = objectTable
            .Where(x => string.Equals(x.Name.TextValue, objectName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => player is null ? float.MaxValue : Vector3.DistanceSquared(x.Position, player.Position))
            .FirstOrDefault();
        if (target is null || player is null || Vector3.Distance(target.Position, player.Position) > maximumDistance)
            return false;
        var targetSystem = TargetSystem.Instance();
        return targetSystem != null && targetSystem->InteractWithObject((GameObject*)target.Address, false) != 0;
    }

    public bool RequestListings(uint itemId)
    {
        if (itemId == 0 || !dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return false;

        var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        var agent = AgentItemSearch.Instance();
        if (addon == null || !addon->IsVisible || addon->SearchTextInput == null ||
            addon->ResultsList == null || agent == null)
            return false;

        if (visibleSearchItemId == itemId)
            return true;

        ResetListingRequest();
        addon->SearchTextInput->SetText(item.Name.ToString());
        addon->RunSearch(false);
        visibleSearchItemId = itemId;
        visibleSearchResultSelected = false;
        return true;
    }

    public bool AreListingsReady(uint itemId)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || visibleSearchItemId != itemId)
            return false;

        var resultAddon = gameGui.GetAddonByName<AtkUnitBase>("ItemSearchResult");
        if (resultAddon != null && resultAddon->IsVisible)
        {
            if (proxy->SearchItemId == itemId)
                return !proxy->WaitingForListings;

            // Never accept data from a previously opened item. Wait for an
            // in-flight request, or close the stale result so the exact search
            // row can be activated below.
            if (proxy->WaitingForListings)
                return false;
            resultAddon->Close(true);
        }

        if (proxy->WaitingForListings)
            return false;

        if (visibleSearchResultSelected && DateTimeOffset.UtcNow < nextVisibleSelectionAt)
            return false;
        visibleSearchResultSelected = false;

        var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        var agent = AgentItemSearch.Instance();
        if (addon == null || !addon->IsVisible || addon->ResultsList == null || agent == null ||
            !agent->ListingPageLoaded)
            return false;

        var ids = agent->ListingPageItemIds;
        var count = (int)Math.Min(agent->ListingPageItemCount, (uint)ids.Length);
        count = Math.Min(count, addon->ResultsList->GetItemCount());
        for (var index = 0; index < count; index++)
        {
            if (ids[index] != itemId || addon->ResultsList->GetItemDisabledState(index))
                continue;

            // SelectItem(..., true) emits ListItemSelect, which only highlights
            // this list. A real user click emits ListItemClick and is what opens
            // ItemSearchResult and starts the server listing request.
            addon->ResultsList->SelectItem(index);
            addon->ResultsList->DispatchItemEvent(index, AtkEventType.ListItemClick);
            visibleSearchResultSelected = true;
            nextVisibleSelectionAt = DateTimeOffset.UtcNow.AddMilliseconds(2_500);
            return false;
        }
        return false;
    }

    public void ResetListingRequest()
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy != null)
        {
            if (proxy->WaitingForListings)
                proxy->EndRequest();
            proxy->ClearListData();
        }
        CloseAddon("ItemSearchResult");
        visibleSearchItemId = 0;
        visibleSearchResultSelected = false;
        nextVisibleSelectionAt = default;
    }

    public IReadOnlyList<LivePurchaseListing> ReadLiveListings(uint itemId)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->WaitingForListings || proxy->SearchItemId != itemId)
            return [];

        var result = new List<LivePurchaseListing>();
        var source = proxy->Listings;
        var count = (int)Math.Min(proxy->ListingCount, (uint)source.Length);
        for (var index = 0; index < count; index++)
        {
            var row = source[index];
            if (row.ItemId == itemId && row.UnitPrice > 0 && row.Quantity > 0)
                result.Add(new(index, row.ItemId, row.ListingId, row.RetainerId,
                    row.UnitPrice, row.Quantity, row.IsHqItem, row.TotalTax));
        }
        return result;
    }

    public bool TrySelectLiveListing(
        ProcurementOrder expected,
        IReadOnlySet<ulong> excludedRetainerIds,
        out LivePurchaseListing? listing)
    {
        listing = null;
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->WaitingForListings || proxy->SearchItemId != expected.ItemId)
            return false;

        var candidates = new List<LivePurchaseListing>();
        var source = proxy->Listings;
        var count = (int)Math.Min(proxy->ListingCount, (uint)source.Length);
        for (var index = 0; index < count; index++)
        {
            var row = source[index];
            if (row.ItemId != expected.ItemId || row.UnitPrice == 0 || row.UnitPrice > expected.MaximumAcceptableUnitPrice ||
                row.Quantity == 0 || row.Quantity > expected.Quantity || row.IsHqItem != expected.IsHighQuality ||
                excludedRetainerIds.Contains(row.RetainerId))
                continue;
            candidates.Add(new(index, row.ItemId, row.ListingId, row.RetainerId,
                row.UnitPrice, row.Quantity, row.IsHqItem, row.TotalTax));
        }

        listing = candidates.FirstOrDefault(x => x.ListingId == expected.ListingId && x.RetainerId == expected.RetainerId)
                  ?? candidates.OrderBy(x => x.PricePerUnit).ThenBy(x => x.Quantity).FirstOrDefault();
        return listing is not null;
    }

    public bool SubmitPurchase(LivePurchaseListing listing)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->WaitingForListings || proxy->SearchItemId != listing.ItemId ||
            !IsAddonVisible("ItemSearchResult") || listing.Index < 0 ||
            listing.Index >= proxy->ListingCount || listing.Index >= proxy->Listings.Length)
            return false;
        var source = proxy->Listings;
        fixed (MarketBoardListing* rows = source)
        {
            var row = &rows[listing.Index];
            if (row->ListingId != listing.ListingId || row->RetainerId != listing.RetainerId ||
                row->ItemId != listing.ItemId || row->UnitPrice != listing.PricePerUnit ||
                row->Quantity != listing.Quantity || row->IsHqItem != listing.IsHighQuality ||
                row->TotalTax != listing.TotalTax)
                return false;
            return proxy->SetLastPurchasedItem(row) && proxy->SendPurchaseRequestPacket();
        }
    }

    public void CloseMarketBoard()
    {
        ResetListingRequest();
        CloseAddon("ItemSearchResult");
        CloseAddon("ItemSearch");
    }

    public void CloseRetainerList() => CloseAddon("RetainerList");

    private bool IsAddonVisible(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    private void CloseAddon(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }
}
