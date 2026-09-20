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
    // Completeness is a property of the response, not of what is on screen right
    // now. Once the server's declared rows have all arrived and been seen, that
    // fact is latched for this request generation: the client removes a purchased
    // row from the visible list, and re-deriving completeness from the shrunken
    // count would declare a finished response incomplete and wait forever.
    private bool complete;
    private DateTimeOffset completedAt;
    private int generation;

    /// <summary>Increments whenever a new listing request begins. Reuse is scoped to one generation.</summary>
    public int Generation { get { lock (sync) return generation; } }

    public void Begin(uint itemId)
    {
        lock (sync)
        {
            if (request is { } previous) Remember(previous);
            item = itemId;
            request = expected = null;
            error = 0;
            listings.Clear();
            complete = false;
            completedAt = default;
            generation++;
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
        {
            if (item == 0 || item != itemId || error != 0)
                return false;
            // Already proven complete for this generation. Rows the client has since
            // removed - a stack we just bought - cannot un-finish the response.
            if (complete)
                return true;
            if (expected is not { } count || listings.Count < count || visibleCount < count)
                return false;
            // Counted rows prove a non-empty response is complete. Only an empty
            // acknowledgement needs a quiet period to avoid racing rows.
            if (count == 0 && time.GetUtcNow() - changed < TimeSpan.FromMilliseconds(750))
                return false;
            complete = true;
            // The network observation time, stamped once. Re-reading cached rows
            // must never make an old response look freshly observed.
            completedAt = time.GetUtcNow();
            return true;
        }
    }

    /// <summary>Whether the current generation's response for this item ever completed.</summary>
    public bool IsComplete(uint itemId)
    {
        lock (sync) return complete && item != 0 && item == itemId && error == 0;
    }

    /// <summary>
    /// When the response was observed to be complete - not when it was last read.
    /// Null until it completes, so a caller cannot mistake a pending or superseded
    /// request for fresh data.
    /// </summary>
    public DateTimeOffset? CompletedAt(uint itemId)
    {
        lock (sync) return complete && item != 0 && item == itemId ? completedAt : null;
    }

    /// <summary>
    /// Whether the already-received listings may be reused instead of searching
    /// again. Bounded by the real observation age, so a session cannot be kept
    /// alive indefinitely by repeatedly reading it.
    /// </summary>
    public bool IsReusable(uint itemId, TimeSpan maximumAge)
    {
        lock (sync)
            return complete && item != 0 && item == itemId && error == 0 &&
                   time.GetUtcNow() - completedAt <= maximumAge;
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
