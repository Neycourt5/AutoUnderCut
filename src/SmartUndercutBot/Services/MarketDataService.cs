using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public interface IMarketDataService
{
    Task<MarketSnapshot> GetSnapshotAsync(uint itemId, CancellationToken cancellationToken);
    void ClearCache();
}

public sealed class MarketDataService : IMarketDataService, IDisposable
{
    private readonly ConcurrentDictionary<(uint WorldId, uint ItemId), MarketSnapshot> cache = new();
    private readonly HttpClient httpClient = new();
    private readonly IPlayerState playerState;
    private readonly ConfigurationService configuration;

    public MarketDataService(IPlayerState playerState, ConfigurationService configuration)
    {
        this.playerState = playerState;
        this.configuration = configuration;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmartUndercutBot/1.0 Dalamud");
    }

    public async Task<MarketSnapshot> GetSnapshotAsync(uint itemId, CancellationToken cancellationToken)
    {
        if (!playerState.IsLoaded || !playerState.CurrentWorld.IsValid)
            throw new InvalidOperationException("The current world is not available.");

        var worldId = playerState.CurrentWorld.RowId;
        var key = (worldId, itemId);
        var maximumAge = TimeSpan.FromSeconds(configuration.Current.MaximumMarketDataAgeSeconds);
        if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CapturedAt <= maximumAge)
            return cached with { IsFromCache = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Current.MarketRequestTimeoutSeconds));
        var baseUrl = configuration.Current.MarketApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/{worldId}/{itemId}?listings=40&entries=40";

        try
        {
            var response = await httpClient.GetFromJsonAsync<UniversalisResponse>(url, timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Market API returned an empty document.");
            var now = DateTimeOffset.UtcNow;
            var capturedAt = response.LastUploadTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(response.LastUploadTime)
                : now;
            var listings = response.Listings
                .Where(x => x.PricePerUnit > 0)
                .Select(x => new MarketListing(x.PricePerUnit, x.Quantity, x.Hq, x.RetainerName))
                .ToArray();
            var history = response.RecentHistory.Where(x => x.PricePerUnit > 0).Select(x => x.PricePerUnit).Order().ToArray();
            uint? median = history.Length == 0 ? null : history[history.Length / 2];
            var snapshot = new MarketSnapshot(itemId, capturedAt, listings, median);
            cache[key] = snapshot;
            return snapshot;
        }
        catch when (cache.TryGetValue(key, out cached) && DateTimeOffset.UtcNow - cached.CapturedAt <= maximumAge * 2)
        {
            return cached with { IsFromCache = true };
        }
    }

    public void ClearCache() => cache.Clear();
    public void Dispose() => httpClient.Dispose();

    private sealed class UniversalisResponse
    {
        [JsonPropertyName("lastUploadTime")]
        public long LastUploadTime { get; init; }

        [JsonPropertyName("listings")]
        public List<UniversalisListing> Listings { get; init; } = [];

        [JsonPropertyName("recentHistory")]
        public List<UniversalisSale> RecentHistory { get; init; } = [];
    }

    private sealed class UniversalisListing
    {
        [JsonPropertyName("pricePerUnit")]
        public uint PricePerUnit { get; init; }

        [JsonPropertyName("quantity")]
        public uint Quantity { get; init; }

        [JsonPropertyName("hq")]
        public bool Hq { get; init; }

        [JsonPropertyName("retainerName")]
        public string? RetainerName { get; init; }
    }

    private sealed class UniversalisSale
    {
        [JsonPropertyName("pricePerUnit")]
        public uint PricePerUnit { get; init; }
    }
}
