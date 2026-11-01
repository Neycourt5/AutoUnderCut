using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// How much of a market our listings actually win, measured from the game's own
/// sale messages. Until there is enough of it, purchases are sized on an assumed
/// share instead; sizing on the whole market bought a week of every seller's
/// volume of one item at once.
/// </summary>
public static class CaptureShareEstimator
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    // A login may deliver messages for sales made while nobody was watching. The
    // first minutes of each span are not counted, in either direction.
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinimumWatched = TimeSpan.FromHours(12);
    public const int MinimumSales = 5;
    public const decimal MinimumShare = 0.05m;
    public const decimal MaximumShare = 0.9m;

    /// <summary>
    /// Units our listings sold while the tracker was listening, over what the whole
    /// market sells in the same length of time. Null until the tracker has listened
    /// for long enough and seen enough sales of this line for the figure to mean
    /// anything; clamped so one lucky or quiet stretch cannot swing sizing wildly.
    /// </summary>
    public static decimal? Measure(IReadOnlyList<RetainerSale> sales, IReadOnlyList<WatchedSpan> watched,
        uint itemId, bool highQuality, decimal marketUnitsPerDay, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(watched);
        if (marketUnitsPerDay <= 0)
            return null;
        var since = now - Window;
        var spans = watched
            .Select(x => new WatchedSpan(Later(x.Start + SettleTime, since), Earlier(x.End, now)))
            .Where(x => x.End > x.Start)
            .ToArray();
        var listened = spans.Aggregate(TimeSpan.Zero, (total, x) => total + x.Duration);
        if (listened < MinimumWatched)
            return null;
        var counted = sales.Where(x => x.ItemId == itemId && x.IsHighQuality == highQuality &&
                                       spans.Any(s => x.At >= s.Start && x.At <= s.End))
            .ToArray();
        if (counted.Length < MinimumSales)
            return null;
        var units = counted.Sum(x => (decimal)x.Quantity);
        return Math.Clamp(units / (marketUnitsPerDay * (decimal)listened.TotalDays), MinimumShare, MaximumShare);
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
