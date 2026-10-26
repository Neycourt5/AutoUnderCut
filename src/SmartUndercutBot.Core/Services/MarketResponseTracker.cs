namespace SmartUndercutBot.Core.Services;

// Network callbacks and the framework thread share this snapshot. A visible
// results window is only a shell until the board behind it has stopped changing.
//
// The game's own InfoProxyItemSearch is the authority on which rows the client
// holds - the market UI draws from it - so callers report its row count and read
// rows from it. This class only decides when that board is finished. It used to
// trust a single request id and the last declared count, and since the 2026-09-17
// game hotfix that let a count of 0 or the wrong page stream finish a read early:
// two thirds of reads came back empty and the rest without their cheapest rows.
public sealed class MarketResponseTracker(TimeProvider? clock = null)
{
    /// <summary>Rows per server page; a shorter page ends its stream.</summary>
    public const int PageSize = 10;

    /// <summary>The most rows the client's board can hold.</summary>
    public const int MaximumRows = 100;

    /// <summary>
    /// An empty board is only believed after this much silence. A false empty is
    /// far worse than a slow read: at home it hides the item's resale price, and
    /// away it marks the world as checked.
    /// </summary>
    public static readonly TimeSpan EmptyConfirmationQuiet = TimeSpan.FromMilliseconds(2_500);

    /// <summary>Silence after a full page, which may still be followed by more.</summary>
    public static readonly TimeSpan FullPageQuiet = TimeSpan.FromMilliseconds(1_500);

    /// <summary>Silence after a short page, which ends its stream.</summary>
    public static readonly TimeSpan ShortPageQuiet = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Silence before accepting fewer rows than the server declared. The board
    /// holds at most <see cref="MaximumRows"/> and may never receive the rest, so
    /// this ends the wait instead of timing the read out; the result is partial.
    /// </summary>
    public static readonly TimeSpan ShortfallQuiet = TimeSpan.FromMilliseconds(3_000);

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly HashSet<int> retired = [];
    private readonly Queue<int> retiredOrder = [];
    private readonly HashSet<int> requestIds = [];
    private readonly HashSet<ulong> listings = [];
    private uint item;
    private int? declared;
    private int countNotices;
    private int ignoredCounts;
    private int error;
    private int pages;
    private int lastPageRows = -1;
    private int boardRows = -1;
    private DateTimeOffset started;
    private DateTimeOffset changed;
    // Completeness is a property of the response, not of what is on screen right
    // now. Once the board has been proven finished, that fact is latched for this
    // request generation: the client removes a purchased row from the visible
    // list, and re-deriving completeness from the shrunken count would declare a
    // finished response incomplete and wait forever.
    private bool complete;
    private bool partial;
    private string completion = string.Empty;
    private DateTimeOffset completedAt;
    private int generation;

    /// <summary>Increments whenever a new listing request begins. Reuse is scoped to one generation.</summary>
    public int Generation { get { lock (sync) return generation; } }

    /// <summary>The item whose listings were last requested; zero between requests.</summary>
    public uint CurrentItem { get { lock (sync) return item; } }

    public void Begin(uint itemId)
    {
        lock (sync)
        {
            // Every stream the previous request opened is finished with. Its late
            // pages must not count as activity for the next one.
            foreach (var id in requestIds) Remember(id);
            requestIds.Clear();
            item = itemId;
            declared = null;
            countNotices = ignoredCounts = 0;
            error = 0;
            pages = 0;
            lastPageRows = boardRows = -1;
            listings.Clear();
            complete = partial = false;
            completion = string.Empty;
            completedAt = default;
            generation++;
            started = changed = time.GetUtcNow();
        }
    }

    public void ReceiveCount(uint itemId, int count, int errorCode) => ReceiveCount(itemId, itemId, count, errorCode);

    /// <summary>
    /// A declared row count from the game's request-result handler. The item is
    /// read both before and after the native handler runs, so a client that only
    /// updates the search item inside the handler is still attributed correctly.
    /// Several notices may arrive for one request; the largest is the board the
    /// server described, so a later zero can never shrink it.
    /// </summary>
    public void ReceiveCount(uint itemBefore, uint itemAfter, int count, int errorCode)
    {
        lock (sync)
        {
            if (item == 0 || (itemBefore != item && itemAfter != item))
            {
                ignoredCounts++;
                return;
            }
            countNotices++;
            declared = Math.Max(declared ?? 0, Math.Clamp(count, 0, MaximumRows));
            error = errorCode;
            changed = time.GetUtcNow();
        }
    }

    /// <summary>
    /// One page of offerings. Pages for this item are accepted from any request id
    /// that began after <see cref="Begin"/>: a board can arrive as more than one
    /// stream, and keeping only the first id is what dropped the cheapest rows.
    /// </summary>
    public void ReceiveRows(int requestId, uint itemId, IEnumerable<ulong> ids, int pageRows = -1)
    {
        lock (sync)
        {
            if (retired.Contains(requestId)) return;
            if (itemId == 0)
            {
                // An empty page carries no item id. It can only be ours if it belongs
                // to a stream already seen this generation; then it ends that stream.
                if (item != 0 && requestIds.Contains(requestId))
                {
                    lastPageRows = 0;
                    changed = time.GetUtcNow();
                }
                return;
            }
            if (item == 0 || item != itemId) { Remember(requestId); return; }
            requestIds.Add(requestId);
            pages++;
            var added = 0;
            foreach (var id in ids)
            {
                added++;
                if (id != 0) listings.Add(id);
            }
            lastPageRows = pageRows >= 0 ? pageRows : added;
            changed = time.GetUtcNow();
        }
    }

    /// <summary>
    /// Whether the board for <paramref name="itemId"/> is finished.
    /// <paramref name="rowsOnBoard"/> is how many rows for this item the client's
    /// board holds right now; it is the source of truth for what can be read.
    /// </summary>
    public bool IsReady(uint itemId, int rowsOnBoard)
    {
        lock (sync)
        {
            if (item == 0 || item != itemId || error != 0)
                return false;
            // Already proven complete for this generation. Rows the client has since
            // removed - a stack we just bought - cannot un-finish the response.
            if (complete)
                return true;
            var now = time.GetUtcNow();
            var rows = Math.Clamp(rowsOnBoard, 0, MaximumRows);
            if (rows != boardRows)
            {
                // Rows appearing or leaving is activity; first noticing an empty
                // board is not, or every poll's first look would restart the wait.
                if (boardRows >= 0 || rows > 0)
                    changed = now;
                boardRows = rows;
            }
            // Nothing from the server for this request yet. Rows already on the board
            // could only be left over from an earlier one, so they prove nothing.
            if (countNotices == 0 && pages == 0)
                return false;
            if (rows >= MaximumRows)
                return Finish(now, "the board is full", false);
            // A board can arrive as more than one stream, each declaring its own
            // count, and the first to finish is not necessarily the cheapest. A
            // short settle lets a stream that is already under way land first.
            if (declared is > 0 and var count && rows >= count)
                return now - changed >= ShortPageQuiet && Finish(now, "every declared row arrived", false);
            // Rows are owed - declared by the server or seen arriving - but the board
            // holds none of them. Reading now would report an empty market that is
            // not empty; let the route time the request out and retry it.
            if (rows == 0 && (declared > 0 || listings.Count > 0))
                return false;
            var shortfall = declared is > 0 and var expected && rows < expected;
            var quiet = rows == 0 ? EmptyConfirmationQuiet
                : shortfall ? ShortfallQuiet
                : lastPageRows is >= 0 and < PageSize ? ShortPageQuiet
                : FullPageQuiet;
            if (now - changed < quiet)
                return false;
            return Finish(now,
                rows == 0 ? "the board stayed empty"
                    : shortfall ? $"rows stopped at {rows} of {declared} declared"
                    : lastPageRows is >= 0 and < PageSize ? "a short final page arrived"
                    : "no further pages arrived",
                shortfall);
        }
    }

    private bool Finish(DateTimeOffset now, string reason, bool isPartial)
    {
        complete = true;
        partial = isPartial;
        completion = reason;
        // The network observation time, stamped once. Re-reading cached rows
        // must never make an old response look freshly observed.
        completedAt = now;
        return true;
    }

    /// <summary>Whether the current generation's response for this item ever completed.</summary>
    public bool IsComplete(uint itemId)
    {
        lock (sync) return complete && item != 0 && item == itemId && error == 0;
    }

    /// <summary>
    /// Whether the completed board holds fewer rows than the server declared. The
    /// cheapest rows arrive first, so a partial board still prices the market, but
    /// it says less about how deep it is.
    /// </summary>
    public bool IsPartial(uint itemId)
    {
        lock (sync) return complete && partial && item != 0 && item == itemId;
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

    /// <summary>How long ago the current request for this item was sent; null for any other item.</summary>
    public TimeSpan? RequestAge(uint itemId)
    {
        lock (sync) return item != 0 && item == itemId ? time.GetUtcNow() - started : null;
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

    public string Summary
    {
        get
        {
            lock (sync)
                return $"server rows {declared?.ToString() ?? "pending"}, received {listings.Count}, " +
                       $"board {Math.Max(0, boardRows)}, error {error}";
        }
    }

    /// <summary>
    /// Everything this request observed, for one session-log line per read. The
    /// packet flow changed under the plugin once already; this line is how the
    /// next change gets diagnosed from a log instead of guessed at.
    /// </summary>
    public string Describe()
    {
        lock (sync)
            return $"declared {declared?.ToString() ?? "none"} from {countNotices} notice(s)" +
                   (ignoredCounts > 0 ? $" ({ignoredCounts} for another item)" : string.Empty) +
                   $", error {error}, {pages} page(s) from {requestIds.Count} request id(s), " +
                   $"{listings.Count} row(s) received, board {Math.Max(0, boardRows)}, " +
                   $"last page {(lastPageRows < 0 ? "none" : lastPageRows.ToString())}, " +
                   $"{(time.GetUtcNow() - started).TotalSeconds:N1}s since the request" +
                   (complete ? $"; complete: {completion}{(partial ? " (partial)" : string.Empty)}" : "; not complete");
    }

    private void Remember(int id)
    {
        if (!retired.Add(id)) return;
        retiredOrder.Enqueue(id);
        while (retiredOrder.Count > 32) retired.Remove(retiredOrder.Dequeue());
    }
}
