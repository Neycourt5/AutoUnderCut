using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public sealed record ProcurementLedgerEntry(
    uint ItemId,
    string ItemName,
    uint PurchasedQuantity,
    uint ListedQuantity,
    uint TargetSalePrice,
    int TargetStackSize,
    bool IsHighQuality,
    bool RequireFullStacks = false,
    uint ReserveQuantity = 0,
    bool IsBagStock = false)
{
    public uint PendingQuantity => PurchasedQuantity > ListedQuantity ? PurchasedQuantity - ListedQuantity : 0;
}

public sealed class ProcurementLedger
{
    private readonly object sync = new();
    private readonly Dictionary<(uint ItemId, bool IsHighQuality), ProcurementLedgerEntry> entries = [];

    public IReadOnlyList<ProcurementLedgerEntry> Snapshot()
    {
        lock (sync)
            return entries.Values.OrderBy(x => x.ItemName).ToArray();
    }

    public void RecordPurchase(ProcurementOrder order, int targetStackSize)
    {
        lock (sync)
        {
            var key = (order.ItemId, order.IsHighQuality);
            if (entries.TryGetValue(key, out var existing))
            {
                entries[key] = existing with
                {
                    PurchasedQuantity = existing.PurchasedQuantity + order.Quantity,
                    TargetSalePrice = order.TargetSalePrice,
                    TargetStackSize = targetStackSize,
                };
            }
            else
            {
                entries[key] = new(
                    order.ItemId, order.ItemName, order.Quantity, 0, order.TargetSalePrice, targetStackSize,
                    order.IsHighQuality);
            }
        }
    }

    public void QueueExistingStock(
        uint itemId,
        string itemName,
        uint quantity,
        uint targetSalePrice,
        int targetStackSize,
        bool isHighQuality,
        uint reserveQuantity)
    {
        lock (sync)
        {
            var key = (itemId, isHighQuality);
            entries[key] = new(
                itemId,
                itemName,
                quantity,
                0,
                targetSalePrice,
                targetStackSize,
                isHighQuality,
                true,
                reserveQuantity,
                true);
        }
    }

    public void ClearBagStockQueue()
    {
        lock (sync)
        {
            foreach (var key in entries.Where(x => x.Value.IsBagStock).Select(x => x.Key).ToArray())
                entries.Remove(key);
        }
    }

    public bool TryGetPending(uint itemId, bool isHighQuality, out ProcurementLedgerEntry? entry)
    {
        lock (sync)
        {
            entry = entries.GetValueOrDefault((itemId, isHighQuality));
            return entry?.PendingQuantity > 0;
        }
    }

    public void MarkListed(uint itemId, bool isHighQuality, uint quantity)
    {
        lock (sync)
        {
            var key = (itemId, isHighQuality);
            if (!entries.TryGetValue(key, out var entry))
                return;
            entries[key] = entry with
            {
                ListedQuantity = Math.Min(entry.PurchasedQuantity, entry.ListedQuantity + quantity),
            };
        }
    }

    public void ClearCompleted()
    {
        lock (sync)
        {
            foreach (var key in entries.Where(x => x.Value.PendingQuantity == 0).Select(x => x.Key).ToArray())
                entries.Remove(key);
        }
    }
}
