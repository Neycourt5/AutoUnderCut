using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class ShoppingScoutPolicy
{
    // Two stops per data center per wave: reach all four before working through
    // the remaining worlds. Every world still appears exactly once per circuit.
    public static IReadOnlyList<string> BuildRoute(IReadOnlyList<string> worlds, string home,
        IReadOnlyDictionary<string, decimal> scores)
    {
        var centers = worlds.Chunk(8).Select(dc => dc
            .Where(w => !w.Equals(home, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => scores.GetValueOrDefault(w)).ToArray()).ToArray();
        return Enumerable.Range(0, 4).SelectMany(wave => centers.SelectMany(dc => dc.Skip(wave * 2).Take(2))).ToArray();
    }

    /// <summary>
    /// What to price-check on one away world. The preferred food and potion block is
    /// reserved on every world before anything else is considered: it is the stock
    /// that actually turns over, so a rotation that walks past it to reach the next
    /// materia line is a wasted trip. Only the secondary lines rotate, so each of
    /// them still gets its turn without ever displacing the block.
    /// </summary>
    public static IReadOnlyList<uint> SelectWorldItems(
        IReadOnlyList<uint> preferred, IReadOnlyList<uint> secondary,
        IEnumerable<uint> resumed, IEnumerable<uint> hinted, int worldIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(resumed);
        ArgumentNullException.ThrowIfNull(hinted);
        limit = Math.Max(1, limit);
        var offset = secondary.Count == 0
            ? 0
            : Math.Max(0, worldIndex) * Math.Max(1, limit / 2) % secondary.Count;
        var rotated = secondary.Skip(offset).Concat(secondary.Take(offset));
        return resumed.Concat(preferred).Concat(hinted).Concat(rotated).Distinct().Take(limit).ToArray();
    }

    public static bool IsExceptional(ProcurementOrder order, decimal minimumRoi) =>
        order.PricePerUnit > 0 && order.Quantity > 0 &&
        order.ExpectedProfit >= decimal.Ceiling(order.PricePerUnit * (decimal)order.Quantity * 1.05m) *
            Math.Max(100m, minimumRoi) / 100m;
}
