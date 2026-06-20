using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public sealed record ProcurementLedgerEntry(
    uint ItemId,
    string ItemName,
    uint PurchasedQuantity,
    uint ListedQuantity,
    uint TargetSalePrice,
    int TargetStackSize)
{
    public uint PendingQuantity => PurchasedQuantity > ListedQuantity ? PurchasedQuantity - ListedQuantity : 0;
}

public sealed class ProcurementLedger
{
    private readonly object sync = new();
    private readonly Dictionary<uint, ProcurementLedgerEntry> entries = [];

    public IReadOnlyList<ProcurementLedgerEntry> Snapshot()
    {
        lock (sync)
            return entries.Values.OrderBy(x => x.ItemName).ToArray();
    }

    public void RecordPurchase(ProcurementOrder order, int targetStackSize)
    {
        lock (sync)
        {
            if (entries.TryGetValue(order.ItemId, out var existing))
            {
                entries[order.ItemId] = existing with
                {
                    PurchasedQuantity = existing.PurchasedQuantity + order.Quantity,
                    TargetSalePrice = order.TargetSalePrice,
                    TargetStackSize = targetStackSize,
                };
            }
            else
            {
                entries[order.ItemId] = new(
                    order.ItemId, order.ItemName, order.Quantity, 0, order.TargetSalePrice, targetStackSize);
            }
        }
    }

    public bool TryGetPending(uint itemId, out ProcurementLedgerEntry? entry)
    {
        lock (sync)
        {
            entry = entries.GetValueOrDefault(itemId);
            return entry?.PendingQuantity > 0;
        }
    }

    public void MarkListed(uint itemId, uint quantity)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(itemId, out var entry))
                return;
            entries[itemId] = entry with
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
