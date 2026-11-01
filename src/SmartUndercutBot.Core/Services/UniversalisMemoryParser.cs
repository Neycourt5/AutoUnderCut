using System.Globalization;
using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// Turns Universalis' current-listings answers into price-memory snapshots.
///
/// A regional request (<c>/api/v2/North-America/{ids}?listings=100&amp;hq=true</c>)
/// returns the cheapest rows across every world, each with its world name, so it
/// says exactly where the cheap stock is. Each world's rows are dated by that
/// world's last upload, not by when we asked. Universalis listing and retainer ids
/// are hashes, so our own retainers are recognised by name.
/// </summary>
public static class UniversalisMemoryParser
{
    public static IReadOnlyList<WorldPriceSnapshot> ParseWorldSnapshots(
        JsonElement root, bool highQuality, string homeWorld, int requestedListings, DateTimeOffset fetchedAt)
    {
        var snapshots = new List<WorldPriceSnapshot>();
        foreach (var (itemId, item) in Items(root))
        {
            if (!item.TryGetProperty("listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
                continue;
            var uploads = UploadTimes(item);
            var rows = new List<(string World, uint WorldId, uint Price, uint Quantity, DateTimeOffset? Reviewed)>();
            var returned = 0;
            uint dearest = 0;
            foreach (var listing in listings.EnumerateArray())
            {
                returned++;
                var price = UInt(listing, "pricePerUnit");
                dearest = Math.Max(dearest, price);
                var quantity = UInt(listing, "quantity");
                var world = Text(listing, "worldName");
                if (price == 0 || quantity == 0 || string.IsNullOrWhiteSpace(world) ||
                    Bool(listing, "hq") != highQuality ||
                    string.Equals(world, homeWorld, StringComparison.OrdinalIgnoreCase))
                    continue;
                var reviewedSeconds = Long(listing, "lastReviewTime") ?? 0;
                DateTimeOffset? reviewed = reviewedSeconds is > 0 and < 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds(reviewedSeconds) : null;
                rows.Add((world, UInt(listing, "worldID"), price, quantity, reviewed));
            }
            // A full page means the view stopped at the hundredth row: everything
            // cheaper than its dearest row is present, anything dearer may not be.
            var completeBelow = requestedListings > 0 && returned >= requestedListings ? dearest : 0u;
            foreach (var group in rows.GroupBy(x => x.World, StringComparer.OrdinalIgnoreCase))
            {
                var worldId = group.First().WorldId;
                var observed = uploads.TryGetValue(worldId, out var uploaded) ? uploaded
                    : group.Max(x => x.Reviewed) ?? fetchedAt;
                if (observed > fetchedAt)
                    observed = fetchedAt;
                var cheapest = group.OrderBy(x => x.Price).ThenByDescending(x => x.Quantity)
                    .Take(PriceMemory.MaximumRowsPerSnapshot)
                    .Select(x => new RememberedListing(0, 0, x.Price, x.Quantity)).ToArray();
                snapshots.Add(new(group.First().World, itemId, highQuality, PriceSource.Universalis, observed,
                    group.Count(), group.Aggregate(0UL, (sum, x) => sum + x.Quantity), cheapest, completeBelow));
            }
        }
        return snapshots;
    }

    /// <summary>
    /// The home market for one quality from a single-world request with history:
    /// the cheapest competing listing, competing depth, velocity, sales per day and
    /// the median sale price and size of the last week.
    /// </summary>
    public static IReadOnlyList<HomeMarketSnapshot> ParseHomeSnapshots(
        JsonElement root, bool highQuality, IReadOnlySet<string> ownRetainerNames, DateTimeOffset fetchedAt,
        IReadOnlySet<ulong>? ownRetainerIds = null)
    {
        ArgumentNullException.ThrowIfNull(ownRetainerNames);
        bool IsOwn(JsonElement listing) =>
            ownRetainerNames.Contains(Text(listing, "retainerName")) ||
            ownRetainerIds is { Count: > 0 } && ulong.TryParse(Text(listing, "retainerID"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) && ownRetainerIds.Contains(id);
        var snapshots = new List<HomeMarketSnapshot>();
        foreach (var (itemId, item) in Items(root))
        {
            var competing = new List<(uint Price, uint Quantity)>();
            if (item.TryGetProperty("listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                foreach (var listing in listings.EnumerateArray())
                {
                    var price = UInt(listing, "pricePerUnit");
                    var quantity = UInt(listing, "quantity");
                    if (price == 0 || quantity == 0 || Bool(listing, "hq") != highQuality || IsOwn(listing))
                        continue;
                    competing.Add((price, quantity));
                }
            var sold = new List<(uint Price, uint Quantity, DateTimeOffset At)>();
            if (item.TryGetProperty("recentHistory", out var history) && history.ValueKind == JsonValueKind.Array)
                foreach (var sale in history.EnumerateArray())
                {
                    var price = UInt(sale, "pricePerUnit");
                    var quantity = UInt(sale, "quantity");
                    var timestamp = Long(sale, "timestamp") ?? 0;
                    if (price == 0 || quantity == 0 || Bool(sale, "hq") != highQuality ||
                        timestamp is not (> 0 and < 253402300799))
                        continue;
                    sold.Add((price, quantity, DateTimeOffset.FromUnixTimeSeconds(timestamp)));
                }
            var weekAgo = fetchedAt.AddDays(-7);
            var week = sold.Where(x => x.At >= weekAgo && x.At <= fetchedAt).ToArray();
            var spanDays = week.Length == 0 ? 0m
                : (decimal)Math.Max(1d / 24d, (fetchedAt - week.Min(x => x.At)).TotalDays);
            var velocity = Decimal(item, highQuality ? "hqSaleVelocity" : "nqSaleVelocity");
            var unitsPerDay = velocity is > 0 ? velocity.Value
                : spanDays > 0 ? week.Sum(x => (decimal)x.Quantity) / spanDays : 0m;
            if (competing.Count == 0 && week.Length == 0)
                continue;
            var uploadMs = Long(item, "lastUploadTime") ?? 0;
            var uploaded = uploadMs is > 0 and < 253402300799999
                ? DateTimeOffset.FromUnixTimeMilliseconds(uploadMs) : fetchedAt;
            snapshots.Add(new(itemId, highQuality, PriceSource.Universalis, uploaded > fetchedAt ? fetchedAt : uploaded,
                competing.Count == 0 ? 0 : competing.Min(x => x.Price), competing.Count,
                competing.Aggregate(0UL, (sum, x) => sum + x.Quantity), unitsPerDay,
                spanDays > 0 ? week.Length / spanDays : 0m,
                Median(week.Select(x => x.Price)), Median(week.Select(x => x.Quantity))));
        }
        return snapshots;
    }

    private static IEnumerable<(uint ItemId, JsonElement Item)> Items(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
                if (uint.TryParse(item.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 &&
                    item.Value.ValueKind == JsonValueKind.Object)
                    yield return (id, item.Value);
        }
        else if (UInt(root, "itemID") is > 0 and var single)
            yield return (single, root);
    }

    private static Dictionary<uint, DateTimeOffset> UploadTimes(JsonElement item)
    {
        var times = new Dictionary<uint, DateTimeOffset>();
        if (!item.TryGetProperty("worldUploadTimes", out var uploads) || uploads.ValueKind != JsonValueKind.Object)
            return times;
        foreach (var upload in uploads.EnumerateObject())
            if (uint.TryParse(upload.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var worldId) &&
                upload.Value.ValueKind == JsonValueKind.Number && upload.Value.TryGetInt64(out var ms) &&
                ms is > 0 and < 253402300799999)
                times[worldId] = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return times;
    }

    private static uint Median(IEnumerable<uint> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (uint)(((ulong)sorted[middle - 1] + sorted[middle]) / 2);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0);

    private static uint UInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= uint.MaxValue
            ? (uint)number : 0;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) ? number : null;

    private static decimal? Decimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var number) && number is >= 0 and <= 1_000_000_000m ? number : null;
}
