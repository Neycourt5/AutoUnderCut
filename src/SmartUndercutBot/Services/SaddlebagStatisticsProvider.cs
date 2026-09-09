using System.Net.Http.Json;
using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

/// <summary>
/// Optional discovery source. Saddlebag Exchange publishes a whole-region market
/// statistics dataset that is refreshed roughly once a day, which makes it useful
/// for deciding what deserves attention and useless for approving a purchase.
/// Several of their other endpoints call Universalis; live market queries are
/// deliberately not routed through here.
/// </summary>
public sealed class SaddlebagStatisticsProvider : IMarketStatisticsProvider, IDisposable
{
    private const string Endpoint = "https://docs.saddlebagexchange.com/api/ffxivrawstats";

    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly Func<bool> isEnabled;
    private readonly Func<string> region;
    private readonly AutomationLog log;

    public SaddlebagStatisticsProvider(Func<bool> isEnabled, Func<string> region, AutomationLog log)
    {
        this.isEnabled = isEnabled;
        this.region = region;
        this.log = log;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmartUndercutter/1.0");
    }

    public string Name => "Saddlebag Exchange";
    public bool IsEnabled => isEnabled();

    public async Task<IReadOnlyList<MarketStatistic>> GetStatisticsAsync(
        IReadOnlyList<uint> itemIds, string scope, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return [];
        // -1 asks for the whole market, which is the point of a daily dataset.
        var ids = itemIds is { Count: > 0 } ? itemIds.Select(x => (long)x).ToArray() : [-1L];
        var payload = new Dictionary<string, object>
        {
            ["region"] = string.IsNullOrWhiteSpace(scope) ? region() : scope,
            ["item_ids"] = ids,
        };
        try
        {
            using var response = await httpClient.PostAsJsonAsync(Endpoint, payload, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Add(AutomationLogLevel.Warning,
                    $"MARKET DISCOVERY: {Name} answered {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                    "Curated rules and live market checks are unaffected.");
                return [];
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException ||
                                   ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Never a hard dependency: log clearly and carry on with what we have.
            log.Add(AutomationLogLevel.Warning,
                $"MARKET DISCOVERY: {Name} was unavailable ({ex.GetType().Name}: {ex.Message}). " +
                "Curated rules and live market checks are unaffected.");
            return [];
        }
    }

    /// <summary>
    /// The dataset is an object keyed by item id, each value carrying NQ/HQ medians
    /// and the quantity sold in the sampled window, plus the update timestamp.
    /// </summary>
    internal static IReadOnlyList<MarketStatistic> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];
        var statistics = new List<MarketStatistic>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;
            var item = property.Value;
            var itemId = ReadUInt(item, "itemID");
            if (itemId == 0 && !uint.TryParse(property.Name, out itemId))
                continue;
            var updated = ReadDouble(item, "lastUpdateTimeUnix");
            // The window the quantities cover is not published, so treat the daily
            // dataset as a day of sales. It ranks candidates; it never prices a buy.
            statistics.Add(new(
                itemId,
                item.TryGetProperty("itemName", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? string.Empty
                    : string.Empty,
                ReadUInt(item, "medianNQ"),
                ReadUInt(item, "medianHQ"),
                ReadDecimal(item, "quantitySoldNQ"),
                ReadDecimal(item, "quantitySoldHQ"),
                updated > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)updated)
                    : DateTimeOffset.UnixEpoch));
        }
        return statistics;
    }

    private static uint ReadUInt(JsonElement element, string name) =>
        (uint)Math.Clamp(ReadDouble(element, name), 0, uint.MaxValue);

    private static decimal ReadDecimal(JsonElement element, string name) =>
        (decimal)Math.Clamp(ReadDouble(element, name), 0, 1_000_000_000);

    private static double ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0
            ? number
            : 0;

    public void Dispose() => httpClient.Dispose();
}
