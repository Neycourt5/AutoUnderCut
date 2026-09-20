namespace SmartUndercutBot.Core.Services;

/// <summary>
/// A world where stock was actually bought and more was probably still on offer.
///
/// Running out of gil part-way through a cheap market is the common case: the
/// listings do not vanish because the wallet emptied, so the next trip should go
/// straight back rather than rediscovering the same world twenty stops later.
/// </summary>
public sealed class BargainMemory
{
    public string WorldName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHighQuality { get; set; }
    /// <summary>The unit price actually paid, so a revisit can tell whether the market moved.</summary>
    public uint UnitPrice { get; set; }
    public uint UnitsBought { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    /// <summary>Why buying stopped: what made this worth coming back to.</summary>
    public BargainStopReason StopReason { get; set; }
}

public enum BargainStopReason
{
    /// <summary>The trip simply moved on - worth a revisit, but nothing was blocked.</summary>
    TripEnded,
    /// <summary>The wallet emptied. The strongest signal that stock remains.</summary>
    OutOfGil,
    /// <summary>Bag or retainer space ran out.</summary>
    OutOfSpace,
}

/// <summary>
/// Remembers productive markets between trips and decides which worlds the next
/// circuit should open with. Deliberately small and short-lived: this is price
/// knowledge, and stale knowledge is worse than none.
/// </summary>
public static class BargainMemoryPolicy
{
    /// <summary>At most this many markets are carried, so a circuit is never dominated by them.</summary>
    public const int MaximumRemembered = 8;

    /// <summary>Entries still young enough to act on.</summary>
    public static IReadOnlyList<BargainMemory> Fresh(
        IEnumerable<BargainMemory>? saved, DateTimeOffset now, TimeSpan maximumAge)
    {
        if (saved is null)
            return [];
        return saved
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.WorldName) && x.ItemId != 0 &&
                        x.UnitsBought > 0 && now - x.ObservedAt <= maximumAge && x.ObservedAt <= now)
            .ToArray();
    }

    /// <summary>
    /// Worlds the next circuit should visit first, best first. A market that took
    /// real capital before it was cut short outranks one that gave up a single
    /// stack, and being stopped by gil or space outranks merely moving on.
    /// </summary>
    public static IReadOnlyList<string> RevisitOrder(IEnumerable<BargainMemory>? fresh) =>
        (fresh ?? [])
            .GroupBy(x => x.WorldName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                World: g.Key,
                Blocked: g.Any(x => x.StopReason != BargainStopReason.TripEnded),
                Value: g.Sum(x => (decimal)x.UnitPrice * x.UnitsBought),
                Newest: g.Max(x => x.ObservedAt)))
            .OrderByDescending(x => x.Blocked)
            .ThenByDescending(x => x.Value)
            .ThenByDescending(x => x.Newest)
            .Select(x => x.World)
            .ToArray();

    /// <summary>Items worth pricing again on a revisited world.</summary>
    public static IReadOnlyList<uint> RevisitItems(IEnumerable<BargainMemory>? fresh, string world) =>
        (fresh ?? [])
            .Where(x => x.WorldName.Equals(world, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => (decimal)x.UnitPrice * x.UnitsBought)
            .Select(x => x.ItemId)
            .Distinct()
            .ToArray();

    /// <summary>
    /// Folds this trip's results into what was already remembered.
    ///
    /// A market that was swept clean is forgotten: its listings are gone, and
    /// sending the next trip back would waste the first stop. Everything else is
    /// replaced by the newer observation, then trimmed to the strongest entries.
    /// </summary>
    public static List<BargainMemory> Merge(
        IEnumerable<BargainMemory>? existing,
        IEnumerable<BargainMemory>? observed,
        IEnumerable<(string World, uint ItemId, bool IsHighQuality)>? exhausted,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var dead = new HashSet<(string, uint, bool)>(
            (exhausted ?? []).Select(x => (x.World.ToUpperInvariant(), x.ItemId, x.IsHighQuality)));
        var merged = new Dictionary<(string, uint, bool), BargainMemory>();

        void Add(BargainMemory entry)
        {
            var key = (entry.WorldName.ToUpperInvariant(), entry.ItemId, entry.IsHighQuality);
            if (dead.Contains(key))
                return;
            // A newer sighting always wins; the older one describes a board that
            // has since been bought from.
            if (merged.TryGetValue(key, out var kept) && kept.ObservedAt >= entry.ObservedAt)
                return;
            merged[key] = entry;
        }

        foreach (var entry in Fresh(existing, now, maximumAge)) Add(entry);
        foreach (var entry in Fresh(observed, now, maximumAge)) Add(entry);

        return merged.Values
            .OrderByDescending(x => x.StopReason != BargainStopReason.TripEnded)
            .ThenByDescending(x => (decimal)x.UnitPrice * x.UnitsBought)
            .ThenByDescending(x => x.ObservedAt)
            .Take(MaximumRemembered)
            .ToList();
    }
}
