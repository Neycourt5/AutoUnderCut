using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

/// <summary>
/// Saddlebag Exchange, an optional intelligence source. Nothing it returns can
/// approve a purchase; it only decides what deserves attention. Facts below were
/// verified against live answers on 2026-11-01:
/// <list type="bullet">
/// <item>A non-empty User-Agent is required (401 without one), and gzip is only
/// sent when asked for (4.4 MB plain against 0.49 MB for the whole-market dataset).</item>
/// <item><c>/api/ffxivrawstats</c> is a seven-day, region-wide dataset refreshed
/// daily: quantities are units sold across the region in a week, not one world's day.</item>
/// <item><c>/api/ffxivmarketshare</c> ranks one world's markets over a chosen window,
/// with units, sales count, average, median, current floor and a trend label.</item>
/// </list>
/// Several Saddlebag endpoints call Universalis themselves; live market queries are
/// deliberately not routed through here.
/// </summary>
public sealed class SaddlebagStatisticsProvider : IMarketStatisticsProvider, IDisposable
{
    private const string Host = "https://api.saddlebagexchange.com";
    private const string RawStatsEndpoint = Host + "/api/ffxivrawstats";
    private const string MarketshareEndpoint = Host + "/api/ffxivmarketshare";

    /// <summary>The raw dataset's window. Its quantities are converted to daily rates with this.</summary>
    public const decimal RawStatsWindowDays = 7m;

    private readonly HttpClient httpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly SemaphoreSlim requestSlots = new(1);
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
        using var document = await PostAsync(RawStatsEndpoint, payload, "MARKET DISCOVERY", cancellationToken)
            .ConfigureAwait(false);
        return document is null ? [] : Parse(document.RootElement);
    }

    /// <summary>
    /// What sold on one world over the last <paramref name="hours"/>, ranked by
    /// <paramref name="sortBy"/>. Category <c>0</c> means everything; 43 is medicine
    /// and 45 meals. Returns null when Saddlebag does not answer.
    /// </summary>
    public Task<JsonDocument?> MarketshareAsync(string world, int hours, int minimumSales, int minimumAveragePrice,
        IReadOnlyList<int> filters, string sortBy, CancellationToken token) =>
        PostAsync(MarketshareEndpoint, new Dictionary<string, object>
        {
            ["server"] = world,
            ["time_period"] = Math.Clamp(hours, 1, 24 * 30),
            ["sales_amount"] = Math.Max(0, minimumSales),
            ["average_price"] = Math.Max(0, minimumAveragePrice),
            ["filters"] = filters is { Count: > 0 } ? filters.ToArray() : new[] { 0 },
            ["sort_by"] = string.IsNullOrWhiteSpace(sortBy) ? "marketValue" : sortBy,
        }, "MARKET SCAN", token);

    private async Task<JsonDocument?> PostAsync(string endpoint, object payload, string prefix, CancellationToken token)
    {
        await requestSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(endpoint, payload, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Add(AutomationLogLevel.Warning,
                    $"{prefix}: {Name} answered {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                    "Curated rules and live market checks are unaffected.");
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException ||
                                   ex is TaskCanceledException && !token.IsCancellationRequested)
        {
            // Never a hard dependency: log clearly and carry on with what we have.
            log.Add(AutomationLogLevel.Warning,
                $"{prefix}: {Name} was unavailable ({ex.GetType().Name}: {ex.Message}). " +
                "Curated rules and live market checks are unaffected.");
            return null;
        }
        finally
        {
            requestSlots.Release();
        }
    }

    /// <summary>
    /// The raw dataset is an object keyed by item id, each value carrying NQ/HQ
    /// medians and the units sold across the region in the last seven days, plus
    /// that item's update timestamp. Units are converted to a daily rate; the result
    /// still describes the whole region, never one world.
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
            statistics.Add(new(
                itemId,
                item.TryGetProperty("itemName", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? string.Empty
                    : string.Empty,
                ReadUInt(item, "medianNQ"),
                ReadUInt(item, "medianHQ"),
                ReadDecimal(item, "quantitySoldNQ") / RawStatsWindowDays,
                ReadDecimal(item, "quantitySoldHQ") / RawStatsWindowDays,
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

    public void Dispose()
    {
        httpClient.Dispose();
        requestSlots.Dispose();
    }
}
