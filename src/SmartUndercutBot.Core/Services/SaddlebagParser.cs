using System.Globalization;
using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>One row of Saddlebag Exchange's per-world "marketshare" ranking.</summary>
/// <param name="UnitsSold">Units sold on the world inside the requested window.</param>
/// <param name="Sales">Number of sales (transactions) inside the window.</param>
/// <param name="MinPrice">Cheapest current listing on the world, zero when out of stock.</param>
/// <param name="State">Saddlebag's trend label: stable, increasing, spiking, decreasing, crashing or "out of stock".</param>
public sealed record MarketShareRow(
    uint ItemId,
    string Name,
    decimal Average,
    decimal Median,
    uint MinPrice,
    decimal UnitsSold,
    decimal Sales,
    decimal MarketValue,
    string State);

/// <summary>
/// Parsers for Saddlebag Exchange responses, verified against live answers on
/// 2026-11-01. Saddlebag sends several numbers as JSON strings (item ids, sale
/// rates, millisecond timestamps), so every read accepts either form, and an empty
/// result is <c>{}</c> rather than an empty list.
/// </summary>
public static class SaddlebagParser
{
    /// <summary><c>POST /api/ffxivmarketshare</c>: <c>{"data":[{...}, ...]}</c>.</summary>
    public static IReadOnlyList<MarketShareRow> ParseMarketshare(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return [];
        var rows = new List<MarketShareRow>();
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            var itemId = (uint)Math.Clamp(Number(row, "itemID") ?? 0m, 0m, uint.MaxValue);
            if (itemId == 0)
                continue;
            rows.Add(new(itemId,
                row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "",
                Number(row, "avg") ?? 0m,
                Number(row, "median") ?? 0m,
                (uint)Math.Clamp(Number(row, "minPrice") ?? 0m, 0m, uint.MaxValue),
                Number(row, "quantitySold") ?? 0m,
                Number(row, "purchaseAmount") ?? 0m,
                Number(row, "marketValue") ?? 0m,
                row.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                    ? state.GetString() ?? "" : ""));
        }
        return rows;
    }

    /// <summary>
    /// Turn a marketshare window into daily rates. The window is the request's
    /// <c>time_period</c> in hours; a row out of stock keeps its median as the price
    /// signal rather than the sentinel zero.
    /// </summary>
    public static MarketCandidate ToCandidate(MarketShareRow row, decimal windowHours, bool highQuality,
        string category, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(row);
        var days = Math.Max(1m / 24m, windowHours / 24m);
        var median = (uint)Math.Clamp(decimal.Round(row.Median > 0 ? row.Median : row.Average), 0m, uint.MaxValue);
        var homePrice = row.MinPrice > 0 && median > 0 ? Math.Min(row.MinPrice, median) : Math.Max(row.MinPrice, median);
        return new(row.ItemId, row.Name, highQuality, category, row.UnitsSold / days, row.Sales / days, homePrice,
            0, string.Empty, row.State, PriceSource.Saddlebag, observedAt);
    }

    private static decimal? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}
