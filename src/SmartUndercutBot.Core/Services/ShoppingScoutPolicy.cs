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

    public static bool IsExceptional(ProcurementOrder order, decimal minimumRoi) =>
        order.PricePerUnit > 0 && order.Quantity > 0 &&
        order.ExpectedProfit >= decimal.Ceiling(order.PricePerUnit * (decimal)order.Quantity * 1.05m) *
            Math.Max(100m, minimumRoi) / 100m;
}
