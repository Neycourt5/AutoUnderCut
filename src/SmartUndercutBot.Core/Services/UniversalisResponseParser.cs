using System.Globalization;
using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class UniversalisResponseParser
{
    public static IReadOnlyList<ProcurementMarketItem> Parse(JsonElement root, IReadOnlyDictionary<uint, string> itemNames)
    {
        var results = new List<ProcurementMarketItem>();
        var worldName = GetString(root, "worldName");
        var worldId = GetUInt32(root, "worldID");
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
                if (uint.TryParse(item.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) &&
                    itemNames.TryGetValue(itemId, out var name))
                    results.Add(ParseItem(itemId, name, item.Value, worldName, worldId));
        }
        else if (GetUInt32(root, "itemID") is var itemId && itemNames.TryGetValue(itemId, out var name))
            results.Add(ParseItem(itemId, name, root, worldName, worldId));
        return results;
    }

    private static ProcurementMarketItem ParseItem(uint itemId, string itemName, JsonElement item,
        string fallbackWorldName, uint fallbackWorldId)
    {
        var itemWorldName = GetString(item, "worldName");
        if (string.IsNullOrWhiteSpace(itemWorldName)) itemWorldName = fallbackWorldName;
        var itemWorldId = GetUInt32(item, "worldID");
        if (itemWorldId == 0) itemWorldId = fallbackWorldId;
        var listings = new List<ProcurementMarketListing>();
        if (item.TryGetProperty("listings", out var listingArray) && listingArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var listing in listingArray.EnumerateArray())
            {
                var worldName = GetString(listing, "worldName");
                if (string.IsNullOrWhiteSpace(worldName)) worldName = itemWorldName;
                var worldId = GetUInt32(listing, "worldID");
                if (worldId == 0) worldId = itemWorldId;
                var price = GetUInt32(listing, "pricePerUnit");
                var quantity = GetUInt32(listing, "quantity");
                if (string.IsNullOrWhiteSpace(worldName) || price == 0 || quantity == 0)
                    continue;
                listings.Add(new(
                    itemId,
                    GetUInt64(listing, "listingID"),
                    GetUInt64(listing, "retainerID"),
                    worldName,
                    worldId,
                    price,
                    quantity,
                    GetBoolean(listing, "hq"),
                    GetString(listing, "listingID")));
            }
        }

        var sales = new List<ProcurementSale>();
        if (item.TryGetProperty("recentHistory", out var historyArray) && historyArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var sale in historyArray.EnumerateArray())
            {
                var price = GetUInt32(sale, "pricePerUnit");
                var quantity = GetUInt32(sale, "quantity");
                var timestamp = GetInt64(sale, "timestamp");
                if (price == 0 || quantity == 0 || timestamp <= 0 || timestamp > 253402300799)
                    continue;
                sales.Add(new(price, quantity, GetBoolean(sale, "hq"), DateTimeOffset.FromUnixTimeSeconds(timestamp)));
            }
        }
        return new(itemId, itemName, listings, sales);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static uint GetUInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               uint.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static ulong GetUInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static long GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return false;
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number != 0;
        return value.ValueKind == JsonValueKind.String &&
               (bool.TryParse(value.GetString(), out var boolean) ? boolean : value.GetString() == "1");
    }

}
