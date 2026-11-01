using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// The plugin's own long-lived record of what markets looked like: the cheap
/// stock it saw on each world, what the home market was doing, our own sales and
/// the latest discovery scan.
///
/// It exists so knowledge survives a reload and so routing can go straight to the
/// worlds that held cheap stock. It never authorizes a purchase: buying still
/// needs a fresh live board read, a fresh home price and every profit guard.
/// Thread-safe; every member takes the same lock.
/// </summary>
public sealed class PriceMemory
{
    /// <summary>Cheapest rows kept per world, item and quality. Deeper rows never decide a route.</summary>
    public const int MaximumRowsPerSnapshot = 20;
    public static readonly TimeSpan WorldRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan HomeRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan SalesRetention = TimeSpan.FromDays(30);
    public const int MaximumWorldSnapshots = 20_000;
    public const int MaximumSales = 5_000;
    public const int MaximumCandidates = 200;
    // A pause shorter than this is the same session; longer means the tracker was
    // not listening (logged out, plugin unloaded) and a new span starts.
    public static readonly TimeSpan WatchGap = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WatchRetention = TimeSpan.FromDays(30);
    public const int MaximumWatchSpans = 2_000;

    private readonly object sync = new();
    private readonly Dictionary<(string World, uint ItemId, bool Hq, PriceSource Source), WorldPriceSnapshot> worlds =
        new(WorldKeyComparer.Instance);
    private readonly Dictionary<(uint ItemId, bool Hq, PriceSource Source), HomeMarketSnapshot> home = [];
    private readonly List<RetainerSale> sales = [];
    private List<MarketCandidate> candidates = [];
    private DateTimeOffset? candidatesObservedAt;
    private readonly List<WatchedSpan> watched = [];
    private DateTimeOffset lastWatchRevisionAt;

    public PriceMemory(PriceMemoryData? data = null)
    {
        if (data is null)
            return;
        foreach (var snapshot in data.Worlds ?? [])
            if (IsValid(snapshot))
                Keep(snapshot with { Cheapest = Normalize(snapshot.Cheapest) });
        foreach (var snapshot in data.Home ?? [])
            if (snapshot is { ItemId: > 0 })
                KeepHome(snapshot);
        foreach (var sale in data.Sales ?? [])
            if (sale is { ItemId: > 0, Quantity: > 0 })
                sales.Add(sale);
        sales.Sort((a, b) => a.At.CompareTo(b.At));
        candidates = (data.Candidates ?? []).Where(x => x is { ItemId: > 0 }).Take(MaximumCandidates).ToList();
        candidatesObservedAt = data.CandidatesObservedAt;
        watched.AddRange((data.Watched ?? []).Where(x => x is not null && x.End >= x.Start).OrderBy(x => x.Start));
    }

    /// <summary>Increments on every change, so a store can tell whether there is anything to save.</summary>
    public long Revision { get; private set; }

    public int WorldSnapshotCount { get { lock (sync) return worlds.Count; } }
    public int HomeSnapshotCount { get { lock (sync) return home.Count; } }
    public int SaleCount { get { lock (sync) return sales.Count; } }

    /// <summary>
    /// Remember what a board held. Rows of the other quality are ignored, the
    /// cheapest are kept, and an older reading never replaces a newer one.
    /// </summary>
    public void RecordWorld(string world, uint itemId, bool highQuality, PriceSource source, DateTimeOffset observedAt,
        IEnumerable<ProcurementMarketListing> rows, uint completeBelowPrice = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(world) || itemId == 0)
            return;
        var matching = rows.Where(x => x.ItemId == itemId && x.IsHighQuality == highQuality &&
                                       x.PricePerUnit > 0 && x.Quantity > 0).ToArray();
        var snapshot = new WorldPriceSnapshot(world.Trim(), itemId, highQuality, source, observedAt,
            matching.Length, matching.Aggregate(0UL, (sum, x) => sum + x.Quantity),
            Normalize(matching.Select(x => new RememberedListing(x.ListingId, x.RetainerId, x.PricePerUnit, x.Quantity))),
            completeBelowPrice);
        lock (sync)
        {
            if (worlds.TryGetValue(Key(snapshot), out var existing) && existing.ObservedAt > observedAt)
                return;
            Keep(snapshot);
            Revision++;
        }
    }

    /// <summary>Remember a snapshot built elsewhere, such as a parsed regional listing view.</summary>
    public void RecordWorld(WorldPriceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValid(snapshot))
            return;
        var normalized = snapshot with { World = snapshot.World.Trim(), Cheapest = Normalize(snapshot.Cheapest) };
        lock (sync)
        {
            if (worlds.TryGetValue(Key(normalized), out var existing) && existing.ObservedAt > normalized.ObservedAt)
                return;
            Keep(normalized);
            Revision++;
        }
    }

    public void RecordHome(HomeMarketSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ItemId == 0)
            return;
        lock (sync)
        {
            if (home.TryGetValue((snapshot.ItemId, snapshot.IsHighQuality, snapshot.Source), out var existing) &&
                existing.ObservedAt > snapshot.ObservedAt)
                return;
            KeepHome(snapshot);
            Revision++;
        }
    }

    /// <summary>
    /// The window inside which an identical sale is one message delivered twice. A
    /// buyer taking two identical stacks is two real sales, and even a fast buyer
    /// needs well over a second between purchases, so the window stays tiny.
    /// </summary>
    public static readonly TimeSpan DuplicateSaleWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>Record one of our sales, ignoring the same message delivered twice.</summary>
    public bool RecordSale(RetainerSale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        if (sale.ItemId == 0 || sale.Quantity == 0)
            return false;
        lock (sync)
        {
            if (sales.Any(x => x.ItemId == sale.ItemId && x.Quantity == sale.Quantity &&
                               x.GilAfterFees == sale.GilAfterFees && x.TownId == sale.TownId &&
                               (x.At - sale.At).Duration() <= DuplicateSaleWindow))
                return false;
            sales.Add(sale);
            if (sales.Count > 1 && sales[^2].At > sale.At)
                sales.Sort((a, b) => a.At.CompareTo(b.At));
            if (sales.Count > MaximumSales)
                sales.RemoveRange(0, sales.Count - MaximumSales);
            Revision++;
            return true;
        }
    }

    public void ReplaceCandidates(IEnumerable<MarketCandidate> found, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(found);
        lock (sync)
        {
            candidates = found.Where(x => x is { ItemId: > 0 }).Take(MaximumCandidates).ToList();
            candidatesObservedAt = observedAt;
            Revision++;
        }
    }

    public IReadOnlyList<MarketCandidate> Candidates { get { lock (sync) return candidates.ToArray(); } }
    public DateTimeOffset? CandidatesObservedAt { get { lock (sync) return candidatesObservedAt; } }

    /// <summary>The newest snapshot of one world, from any source unless one is named.</summary>
    public WorldPriceSnapshot? Latest(string world, uint itemId, bool highQuality, DateTimeOffset now, TimeSpan maximumAge,
        PriceSource? source = null)
    {
        lock (sync)
            return worlds.Values
                .Where(x => x.ItemId == itemId && x.IsHighQuality == highQuality &&
                            string.Equals(x.World, world, StringComparison.OrdinalIgnoreCase) &&
                            (source is null || x.Source == source) && IsFresh(x.ObservedAt, now, maximumAge))
                .MaxBy(x => x.ObservedAt);
    }

    /// <summary>
    /// The newest fresh snapshot per world for one item and quality, cheapest first.
    /// This is the "where did I last see it cheap" question.
    /// </summary>
    public IReadOnlyList<WorldPriceSnapshot> FreshWorlds(uint itemId, bool highQuality, DateTimeOffset now,
        TimeSpan maximumAge, PriceSource? source = null)
    {
        lock (sync)
            return worlds.Values
                .Where(x => x.ItemId == itemId && x.IsHighQuality == highQuality &&
                            (source is null || x.Source == source) && IsFresh(x.ObservedAt, now, maximumAge))
                .GroupBy(x => x.World, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(x => x.ObservedAt)!)
                .Where(x => x.Lowest > 0)
                .OrderBy(x => x.Lowest)
                .ThenBy(x => x.World, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <summary>Every fresh snapshot of one source, newest per world, item and quality.</summary>
    public IReadOnlyList<WorldPriceSnapshot> AllFresh(DateTimeOffset now, TimeSpan maximumAge, PriceSource? source = null)
    {
        lock (sync)
            return worlds.Values
                .Where(x => (source is null || x.Source == source) && IsFresh(x.ObservedAt, now, maximumAge))
                .GroupBy(x => (World: x.World.ToUpperInvariant(), x.ItemId, x.IsHighQuality))
                .Select(g => g.MaxBy(x => x.ObservedAt)!)
                .ToArray();
    }

    /// <summary>The newest home snapshot for an item and quality, live readings winning a tie.</summary>
    public HomeMarketSnapshot? LatestHome(uint itemId, bool highQuality, DateTimeOffset now, TimeSpan maximumAge)
    {
        lock (sync)
            return home.Values
                .Where(x => x.ItemId == itemId && x.IsHighQuality == highQuality && IsFresh(x.ObservedAt, now, maximumAge))
                .OrderByDescending(x => x.ObservedAt)
                .ThenBy(x => x.Source)
                .FirstOrDefault();
    }

    public IReadOnlyList<HomeMarketSnapshot> AllHome()
    {
        lock (sync)
            return home.Values.ToArray();
    }

    /// <summary>
    /// Note that the sale tracker is listening now. Marks within <see cref="WatchGap"/>
    /// of the last one extend its span; a longer silence starts a new span.
    /// </summary>
    public void MarkWatching(DateTimeOffset now)
    {
        lock (sync)
        {
            if (watched.Count > 0)
            {
                var last = watched[^1];
                if (now < last.End)
                    return;
                if (now - last.End <= WatchGap)
                {
                    watched[^1] = last with { End = now };
                    // The end moves on every frame; only every few minutes is it a
                    // change worth saving.
                    if (now - lastWatchRevisionAt >= TimeSpan.FromMinutes(10))
                    {
                        lastWatchRevisionAt = now;
                        Revision++;
                    }
                    return;
                }
            }
            watched.Add(new(now, now));
            if (watched.Count > MaximumWatchSpans)
                watched.RemoveRange(0, watched.Count - MaximumWatchSpans);
            lastWatchRevisionAt = now;
            Revision++;
        }
    }

    public IReadOnlyList<WatchedSpan> WatchedSince(DateTimeOffset since)
    {
        lock (sync)
            return watched.Where(x => x.End >= since).ToArray();
    }

    public IReadOnlyList<RetainerSale> SalesSince(DateTimeOffset since)
    {
        lock (sync)
            return sales.Where(x => x.At >= since).ToArray();
    }

    /// <summary>Our own sales per item and quality inside a window, busiest first.</summary>
    public IReadOnlyList<SalesSummary> SummarizeSales(DateTimeOffset now, TimeSpan window)
    {
        var since = now - window;
        lock (sync)
            return sales.Where(x => x.At >= since && x.At <= now)
                .GroupBy(x => (x.ItemId, x.IsHighQuality))
                .Select(g => new SalesSummary(g.Key.ItemId,
                    g.Select(x => x.ItemName).LastOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
                    g.Key.IsHighQuality, g.Count(), g.Aggregate(0UL, (sum, x) => sum + x.Quantity),
                    g.Aggregate(0UL, (sum, x) => sum + x.GilAfterFees), g.Max(x => x.At)))
                .OrderByDescending(x => x.Sales)
                .ThenByDescending(x => x.GilAfterFees)
                .ToArray();
    }

    /// <summary>Drop what is too old to say anything about today's markets.</summary>
    public int Prune(DateTimeOffset now)
    {
        lock (sync)
        {
            var removed = 0;
            foreach (var key in worlds.Where(x => now - x.Value.ObservedAt > WorldRetention).Select(x => x.Key).ToArray())
                removed += worlds.Remove(key) ? 1 : 0;
            if (worlds.Count > MaximumWorldSnapshots)
                foreach (var key in worlds.OrderBy(x => x.Value.ObservedAt).Take(worlds.Count - MaximumWorldSnapshots)
                             .Select(x => x.Key).ToArray())
                    removed += worlds.Remove(key) ? 1 : 0;
            foreach (var key in home.Where(x => now - x.Value.ObservedAt > HomeRetention).Select(x => x.Key).ToArray())
                removed += home.Remove(key) ? 1 : 0;
            removed += sales.RemoveAll(x => now - x.At > SalesRetention);
            removed += watched.RemoveAll(x => now - x.End > WatchRetention);
            if (removed > 0)
                Revision++;
            return removed;
        }
    }

    public PriceMemoryData ToData()
    {
        lock (sync)
            return new()
            {
                Worlds = worlds.Values.OrderBy(x => x.World, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.ItemId).ThenBy(x => x.IsHighQuality).ThenBy(x => x.Source).ToList(),
                Home = home.Values.OrderBy(x => x.ItemId).ThenBy(x => x.IsHighQuality).ThenBy(x => x.Source).ToList(),
                Sales = sales.ToList(),
                Candidates = candidates.ToList(),
                CandidatesObservedAt = candidatesObservedAt,
                Watched = watched.ToList(),
            };
    }

    private static bool IsFresh(DateTimeOffset observedAt, DateTimeOffset now, TimeSpan maximumAge) =>
        now - observedAt <= maximumAge && observedAt <= now + TimeSpan.FromMinutes(5);

    private static bool IsValid(WorldPriceSnapshot? snapshot) =>
        snapshot is { ItemId: > 0 } && !string.IsNullOrWhiteSpace(snapshot.World);

    private void Keep(WorldPriceSnapshot snapshot) => worlds[Key(snapshot)] = snapshot;

    private void KeepHome(HomeMarketSnapshot snapshot) =>
        home[(snapshot.ItemId, snapshot.IsHighQuality, snapshot.Source)] = snapshot;

    private static (string, uint, bool, PriceSource) Key(WorldPriceSnapshot snapshot) =>
        (snapshot.World, snapshot.ItemId, snapshot.IsHighQuality, snapshot.Source);

    private static RememberedListing[] Normalize(IEnumerable<RememberedListing>? rows) => (rows ?? [])
        .Where(x => x is { PricePerUnit: > 0, Quantity: > 0 })
        .OrderBy(x => x.PricePerUnit)
        .ThenByDescending(x => x.Quantity)
        .Take(MaximumRowsPerSnapshot)
        .ToArray();

    private sealed class WorldKeyComparer : IEqualityComparer<(string World, uint ItemId, bool Hq, PriceSource Source)>
    {
        public static readonly WorldKeyComparer Instance = new();

        public bool Equals((string World, uint ItemId, bool Hq, PriceSource Source) x,
            (string World, uint ItemId, bool Hq, PriceSource Source) y) =>
            x.ItemId == y.ItemId && x.Hq == y.Hq && x.Source == y.Source &&
            string.Equals(x.World, y.World, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string World, uint ItemId, bool Hq, PriceSource Source) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.World), key.ItemId, key.Hq, key.Source);
    }
}
