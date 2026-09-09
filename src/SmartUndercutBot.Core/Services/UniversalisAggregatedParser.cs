using System.Globalization;
using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// Parses Universalis' cached aggregate endpoint,
/// <c>GET /api/v2/aggregated/{worldDcRegion}/{itemIds}</c>. Universalis documents
/// this as the preferred API when individual listings and sales are not needed,
/// and it takes up to 100 item ids per request. It answers "what is worth looking
/// at, and roughly where" - never "buy this listing".
/// </summary>
public static class UniversalisAggregatedParser
{
    /// <summary>
    /// Cheapest-world hints. Each item yields at most one hint per scope level
    /// (world, data centre, region), so a whole region costs a fraction of the
    /// payload of a full listings sweep while still naming worlds worth visiting.
    /// </summary>
    public static IReadOnlyList<MarketPriceHint> ParseHints(
        JsonElement root, Func<uint, string?> worldName, IReadOnlyDictionary<uint, string>? itemNames = null)
    {
        ArgumentNullException.ThrowIfNull(worldName);
        var hints = new List<MarketPriceHint>();
        foreach (var (itemId, entry) in Results(root, itemNames))
        {
            foreach (var quality in new[] { false, true })
            {
                if (!entry.TryGetProperty(quality ? "hq" : "nq", out var side) ||
                    side.ValueKind != JsonValueKind.Object)
                    continue;
                var velocity = BestNumber(side, "dailySaleVelocity", "quantity") ?? 0m;
                if (!side.TryGetProperty("minListing", out var minListing) ||
                    minListing.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var scope in new[] { "world", "dc", "region" })
                {
                    if (!minListing.TryGetProperty(scope, out var value) || value.ValueKind != JsonValueKind.Object)
                        continue;
                    var price = GetUInt32(value, "price");
                    var worldId = GetUInt32(value, "worldId");
                    var name = worldName(worldId);
                    if (price == 0 || string.IsNullOrWhiteSpace(name))
                        continue;
                    hints.Add(new(itemId, name, worldId, price, quality, velocity));
                }
            }
        }
        // The same world can be cheapest at several scope levels.
        return hints.DistinctBy(x => (x.ItemId, x.WorldName, x.IsHighQuality, x.PricePerUnit)).ToArray();
    }

    /// <summary>Velocity and broad pricing, for ranking and discovery.</summary>
    public static IReadOnlyList<MarketStatistic> ParseStatistics(
        JsonElement root, DateTimeOffset observedAt, IReadOnlyDictionary<uint, string>? itemNames = null)
    {
        var statistics = new List<MarketStatistic>();
        foreach (var (itemId, entry) in Results(root, itemNames))
        {
            statistics.Add(new(
                itemId,
                itemNames?.GetValueOrDefault(itemId) ?? string.Empty,
                AveragePrice(entry, "nq"),
                AveragePrice(entry, "hq"),
                Velocity(entry, "nq"),
                Velocity(entry, "hq"),
                observedAt));
        }
        return statistics;
    }

    private static IEnumerable<(uint ItemId, JsonElement Entry)> Results(
        JsonElement root, IReadOnlyDictionary<uint, string>? itemNames)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var entry in results.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var itemId = GetUInt32(entry, "itemId");
            if (itemId == 0 || itemNames is not null && !itemNames.ContainsKey(itemId))
                continue;
            yield return (itemId, entry);
        }
    }

    private static uint AveragePrice(JsonElement entry, string quality) =>
        entry.TryGetProperty(quality, out var side) && side.ValueKind == JsonValueKind.Object
            ? (uint)Math.Clamp(BestNumber(side, "averageSalePrice", "price") ?? 0m, 0m, uint.MaxValue)
            : 0;

    private static decimal Velocity(JsonElement entry, string quality) =>
        entry.TryGetProperty(quality, out var side) && side.ValueKind == JsonValueKind.Object
            ? Math.Clamp(BestNumber(side, "dailySaleVelocity", "quantity") ?? 0m, 0m, 1_000_000_000m)
            : 0m;

    // Prefer the narrowest scope that answered: the home world describes our own
    // market best, the data centre next, and the region last.
    private static decimal? BestNumber(JsonElement side, string metric, string field)
    {
        if (!side.TryGetProperty(metric, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var scope in new[] { "world", "dc", "region" })
            if (value.TryGetProperty(scope, out var scoped) && scoped.ValueKind == JsonValueKind.Object &&
                GetDecimal(scoped, field) is { } number)
                return number;
        return null;
    }

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        decimal number;
        var valid = value.ValueKind == JsonValueKind.Number
            ? value.TryGetDecimal(out number)
            : decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        return valid && number is >= 0 and <= 1_000_000_000m ? number : null;
    }

    private static uint GetUInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number) && number is >= 0 and <= uint.MaxValue)
            return (uint)number;
        return value.ValueKind == JsonValueKind.String &&
               uint.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
