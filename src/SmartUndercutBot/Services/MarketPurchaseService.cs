using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public sealed record LivePurchaseListing(
    int Index,
    uint ItemId,
    ulong ListingId,
    ulong RetainerId,
    uint PricePerUnit,
    uint Quantity,
    bool IsHighQuality);

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
    bool TrySelectLiveListing(ProcurementOrder expected, out LivePurchaseListing? listing);
    bool SubmitPurchase(LivePurchaseListing listing);
    void CloseMarketBoard();
    void CloseRetainerList();
}

public sealed unsafe class MarketPurchaseService : IMarketPurchaseService
{
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;

    public MarketPurchaseService(IObjectTable objectTable, IGameGui gameGui)
    {
        this.objectTable = objectTable;
        this.gameGui = gameGui;
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
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || itemId == 0 || proxy->WaitingForListings)
            return false;
        proxy->ClearListData();
        proxy->SearchItemId = itemId;
        return proxy->RequestData();
    }

    public bool AreListingsReady(uint itemId)
    {
        var proxy = InfoProxyItemSearch.Instance();
        return proxy != null && !proxy->WaitingForListings && proxy->SearchItemId == itemId;
    }

    public bool TrySelectLiveListing(ProcurementOrder expected, out LivePurchaseListing? listing)
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
                row.Quantity == 0 || row.Quantity > expected.Quantity || row.IsHqItem != expected.IsHighQuality)
                continue;
            candidates.Add(new(index, row.ItemId, row.ListingId, row.RetainerId, row.UnitPrice, row.Quantity, row.IsHqItem));
        }

        listing = candidates.FirstOrDefault(x => x.ListingId == expected.ListingId && x.RetainerId == expected.RetainerId)
                  ?? candidates.OrderBy(x => x.PricePerUnit).ThenBy(x => x.Quantity).FirstOrDefault();
        return listing is not null;
    }

    public bool SubmitPurchase(LivePurchaseListing listing)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->WaitingForListings || listing.Index < 0 || listing.Index >= proxy->Listings.Length)
            return false;
        var source = proxy->Listings;
        fixed (MarketBoardListing* rows = source)
        {
            var row = &rows[listing.Index];
            if (row->ListingId != listing.ListingId || row->RetainerId != listing.RetainerId ||
                row->ItemId != listing.ItemId || row->UnitPrice != listing.PricePerUnit || row->Quantity != listing.Quantity)
                return false;
            return proxy->SetLastPurchasedItem(row) && proxy->SendPurchaseRequestPacket();
        }
    }

    public void CloseMarketBoard()
    {
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
