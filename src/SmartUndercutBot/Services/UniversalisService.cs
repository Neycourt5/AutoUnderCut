using System.Globalization;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public interface IUniversalisService
{
    string ResolveDataCenter(string configuredDataCenter);
    IReadOnlyList<ProcurementRule> CreateFavoriteRules();
    Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(
        IReadOnlyList<ProcurementRule> rules,
        string dataCenter,
        CancellationToken cancellationToken);
}

public sealed class UniversalisService : IUniversalisService, IDisposable
{
    private static readonly string[] FavoriteNames =
    [
        "Grade 3 Gemdraught of Strength",
        "Grade 4 Gemdraught of Strength",
        "Grade 3 Gemdraught of Dexterity",
        "Grade 4 Gemdraught of Dexterity",
        "Grade 3 Gemdraught of Intelligence",
        "Grade 4 Gemdraught of Intelligence",
        "Grade 3 Gemdraught of Mind",
        "Grade 4 Gemdraught of Mind",
        "Mate Cookie",
        "Mollete",
        "Caramel Popcorn",
        "Popoto Potage",
    ];

    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;

    public UniversalisService(IPlayerState playerState, IDataManager dataManager)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmartUndercutter/1.0");
    }

    public string ResolveDataCenter(string configuredDataCenter)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataCenter))
            return configuredDataCenter.Trim();
        if (!playerState.IsLoaded)
            return string.Empty;
        var homeWorldId = playerState.HomeWorld.RowId;
        if (homeWorldId == 0 || !dataManager.GetExcelSheet<World>().TryGetRow(homeWorldId, out var world))
            return string.Empty;
        return world.DataCenter.Value.Name.ToString();
    }

    public IReadOnlyList<ProcurementRule> CreateFavoriteRules()
    {
        var names = FavoriteNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return dataManager.GetExcelSheet<Item>()
            .Where(x => names.Contains(x.Name.ToString()))
            .Select(x => new ProcurementRule
            {
                ItemId = x.RowId,
                ItemName = x.Name.ToString(),
                TargetStackSize = 99,
                MaximumSaleSlots = 8,
                MinimumWeeklyUnitsSold = 20,
            })
            .OrderBy(x => x.ItemName)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(
        IReadOnlyList<ProcurementRule> rules,
        string dataCenter,
        CancellationToken cancellationToken)
    {
        var enabled = rules.Where(x => x.Enabled && x.ItemId != 0).DistinctBy(x => x.ItemId).ToArray();
        if (enabled.Length == 0 || string.IsNullOrWhiteSpace(dataCenter))
            return [];

        var itemIds = string.Join(',', enabled.Select(x => x.ItemId));
        var endpoint = $"https://universalis.app/api/v2/{Uri.EscapeDataString(dataCenter)}/{itemIds}" +
                       "?listings=100&entries=100&statsWithin=604800";
        using var response = await httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var ruleNames = enabled.ToDictionary(x => x.ItemId, x => x.ItemName);
        var results = new List<ProcurementMarketItem>();

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
            {
                if (uint.TryParse(item.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
                    results.Add(ParseItem(itemId, ruleNames.GetValueOrDefault(itemId) ?? $"Item #{itemId}", item.Value));
            }
        }
        else if (root.TryGetProperty("itemID", out var itemIdElement) && itemIdElement.TryGetUInt32(out var itemId))
        {
            results.Add(ParseItem(itemId, ruleNames.GetValueOrDefault(itemId) ?? $"Item #{itemId}", root));
        }
        return results;
    }

    private static ProcurementMarketItem ParseItem(uint itemId, string itemName, JsonElement item)
    {
        var listings = new List<ProcurementMarketListing>();
        if (item.TryGetProperty("listings", out var listingArray) && listingArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var listing in listingArray.EnumerateArray())
            {
                var worldName = GetString(listing, "worldName");
                var price = GetUInt32(listing, "pricePerUnit");
                var quantity = GetUInt32(listing, "quantity");
                if (string.IsNullOrWhiteSpace(worldName) || price == 0 || quantity == 0)
                    continue;
                listings.Add(new(
                    itemId,
                    GetUInt64(listing, "listingID"),
                    GetUInt64(listing, "retainerID"),
                    worldName,
                    GetUInt32(listing, "worldID"),
                    price,
                    quantity,
                    GetBoolean(listing, "hq")));
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
                if (price == 0 || quantity == 0 || timestamp <= 0)
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
        if (value.TryGetUInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               uint.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static ulong GetUInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.TryGetUInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static long GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    public void Dispose() => httpClient.Dispose();
}
