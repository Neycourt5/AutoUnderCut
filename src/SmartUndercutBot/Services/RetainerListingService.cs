using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

public sealed record SafetySnapshot(bool IsSafe, string Reason);
public sealed record PriceUpdateResult(bool Succeeded, string Message);

public interface IRetainerListingService
{
    bool IsSellListOpen { get; }
    SafetySnapshot CheckSafety(Vector3 sessionPosition);
    IReadOnlyList<RetainerListing> ReadCurrentListings();
    bool TryReadListing(short slot, out RetainerListing? listing);
    PriceUpdateResult UpdatePrice(RetainerListing expected, uint targetPrice);
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

    public bool IsSellListOpen => IsAddonVisible("RetainerSellList");

    public Vector3? GetPlayerPosition() => objectTable.LocalPlayer?.Position;

    public SafetySnapshot CheckSafety(Vector3 sessionPosition)
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer is null)
            return new(false, "Player logged out or local player is unavailable.");
        if (!IsSellListOpen)
            return new(false, "Retainer sell-list interface closed.");
        if (Vector3.DistanceSquared(objectTable.LocalPlayer.Position, sessionPosition) > 0.0025f)
            return new(false, "Player movement detected.");

        var manager = InventoryManager.Instance();
        var retainerManager = RetainerManager.Instance();
        if (manager == null || retainerManager == null || retainerManager->GetActiveRetainer() == null)
            return new(false, "Retainer state is not available.");
        var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded || container->Size is < 0 or > MaximumRetainerMarketSlots)
            return new(false, "Retainer market inventory is unavailable or malformed.");
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

    public PriceUpdateResult UpdatePrice(RetainerListing expected, uint targetPrice)
    {
        if (targetPrice is 0 or > PricingStrategyService.MaximumListingPrice)
            return new(false, "Target price is outside valid game bounds.");
        if (!TryReadListing(expected.Slot, out var current) || current is null)
            return new(false, "Listing disappeared before commit.");
        if (current.RetainerId != expected.RetainerId || current.ItemId != expected.ItemId ||
            current.Quantity != expected.Quantity || current.CurrentPrice != expected.CurrentPrice ||
            current.IsHighQuality != expected.IsHighQuality)
            return new(false, "Listing changed after evaluation; update was cancelled.");

        var manager = InventoryManager.Instance();
        if (manager == null)
            return new(false, "Inventory manager is unavailable.");

        manager->SetRetainerMarketPrice(expected.Slot, targetPrice);
        return new(true, "The client accepted the retainer price update request.");
    }

    private bool IsAddonVisible(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }
}
