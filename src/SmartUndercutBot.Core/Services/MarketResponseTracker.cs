namespace SmartUndercutBot.Core.Services;

// Network callbacks and the framework thread share this snapshot. A visible
// results window is only a shell until the server's declared rows have arrived.
public sealed class MarketResponseTracker(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly HashSet<int> retired = [];
    private readonly Queue<int> retiredOrder = [];
    private readonly HashSet<ulong> listings = [];
    private uint item;
    private int? request;
    private int? expected;
    private int error;
    private DateTimeOffset changed;

    public void Begin(uint itemId)
    {
        lock (sync)
        {
            if (request is { } previous) Remember(previous);
            item = itemId;
            request = expected = null;
            error = 0;
            listings.Clear();
            changed = time.GetUtcNow();
        }
    }

    public void ReceiveCount(uint itemId, int count, int errorCode)
    {
        lock (sync)
        {
            if (item == 0 || item != itemId) return;
            expected = Math.Clamp(count, 0, 100);
            error = errorCode;
            changed = time.GetUtcNow();
        }
    }

    public void ReceiveRows(int requestId, uint itemId, IEnumerable<ulong> ids)
    {
        lock (sync)
        {
            if (retired.Contains(requestId)) return;
            if (item == 0 || item != itemId) { Remember(requestId); return; }
            if (request is { } current && current != requestId) return;
            request = requestId;
            foreach (var id in ids) if (id != 0) listings.Add(id);
            changed = time.GetUtcNow();
        }
    }

    public bool IsReady(uint itemId, int visibleCount)
    {
        lock (sync)
            return item != 0 && item == itemId && error == 0 && expected is { } count &&
                listings.Count >= count && visibleCount >= count &&
                // Counted rows prove a non-empty response is complete. Only an
                // empty acknowledgement needs a quiet period to avoid racing rows.
                (count > 0 || time.GetUtcNow() - changed >= TimeSpan.FromMilliseconds(750));
    }

    public bool Contains(ulong listingId) { lock (sync) return listings.Contains(listingId); }
    public string Summary { get { lock (sync) return $"server rows {expected?.ToString() ?? "pending"}, received {listings.Count}, error {error}"; } }
    private void Remember(int id)
    {
        if (!retired.Add(id)) return;
        retiredOrder.Enqueue(id);
        while (retiredOrder.Count > 32) retired.Remove(retiredOrder.Dequeue());
    }
}
