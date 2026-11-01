using System.Text.Json;

namespace SmartUndercutBot.Services;

/// <summary>The live HTTP sources behind <see cref="MarketScanService"/>.</summary>
public sealed class MarketScanSource(UniversalisService universalis, SaddlebagStatisticsProvider saddlebag) : IMarketScanSource
{
    public Task<JsonDocument?> RegionListingsAsync(IReadOnlyList<uint> itemIds, string region, bool highQuality,
        int listings, CancellationToken token) =>
        universalis.RegionListingsAsync(itemIds, region, highQuality, listings, token);

    public Task<JsonDocument?> WorldMarketAsync(IReadOnlyList<uint> itemIds, string world, bool highQuality,
        CancellationToken token) =>
        universalis.WorldMarketAsync(itemIds, world, highQuality, token);

    public Task<JsonDocument?> AggregatedAsync(IReadOnlyList<uint> itemIds, string scope, CancellationToken token) =>
        universalis.AggregatedAsync(itemIds, scope, token);

    public Task<JsonDocument?> MarketshareAsync(string world, int hours, int minimumSales, int minimumAveragePrice,
        IReadOnlyList<int> filters, string sortBy, CancellationToken token) =>
        saddlebag.MarketshareAsync(world, hours, minimumSales, minimumAveragePrice, filters, sortBy, token);
}
