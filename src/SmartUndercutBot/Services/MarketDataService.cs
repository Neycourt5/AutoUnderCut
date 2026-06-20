using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public interface IMarketDataService
{
    Task<MarketSnapshot> GetSnapshotAsync(uint itemId, CancellationToken cancellationToken);
    void ClearCache();
}

/// <summary>
/// Captures the market-board packets produced by the game's Compare Prices button.
/// This keeps pricing tied to the live in-game result instead of a delayed web cache.
/// </summary>
public sealed class MarketDataService : IMarketDataService, IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly ConfigurationService configuration;
    private readonly object sync = new();
    private readonly HashSet<int> completedRequestIds = [];
    private readonly Queue<int> completedRequestOrder = [];
    private PendingRequest? pending;

    public MarketDataService(IMarketBoard marketBoard, ConfigurationService configuration)
    {
        this.marketBoard = marketBoard;
        this.configuration = configuration;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
        marketBoard.HistoryReceived += OnHistoryReceived;
    }

    public async Task<MarketSnapshot> GetSnapshotAsync(uint itemId, CancellationToken cancellationToken)
    {
        PendingRequest request;
        lock (sync)
        {
            pending?.Cancel();
            request = new PendingRequest(itemId);
            pending = request;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Current.MarketRequestTimeoutSeconds));
        using var registration = timeout.Token.Register(request.Cancel);

        try
        {
            var listings = await request.Offerings.Task.ConfigureAwait(false);

            // History normally arrives with the same search. Give it a short grace period so
            // the price-war guard can use the live sale history without slowing every item.
            var historyReady = request.History.Task;
            await Task.WhenAny(historyReady, Task.Delay(750, timeout.Token)).ConfigureAwait(false);
            var historical = historyReady.IsCompletedSuccessfully ? historyReady.Result : Array.Empty<uint>();
            uint? median = historical.Length == 0
                ? null
                : historical.Order().ElementAt(historical.Length / 2);

            return new MarketSnapshot(request.ResolvedItemId, DateTimeOffset.UtcNow, listings, median);
        }
        finally
        {
            lock (sync)
            {
                if (request.RequestId is { } requestId)
                    RememberCompletedRequest(requestId);
                if (ReferenceEquals(pending, request))
                    pending = null;
            }
        }
    }

    public void ClearCache()
    {
        lock (sync)
        {
            pending?.Cancel();
            pending = null;
        }
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        PendingRequest? request;
        lock (sync)
        {
            request = pending;
            if (request is null || completedRequestIds.Contains(offerings.RequestId))
                return;
        }

        var receivedItemId = offerings.ItemListings.FirstOrDefault()?.ItemId ?? 0;
        if (request.RequestedItemId != 0 && receivedItemId != 0 && receivedItemId != request.RequestedItemId)
            return;
        request.ResolveItem(receivedItemId);

        var rows = offerings.ItemListings
            .Where(x => request.ResolvedItemId == 0 || x.ItemId == request.ResolvedItemId)
            .Select(x => new MarketListing(
                x.PricePerUnit,
                x.ItemQuantity,
                x.IsHq,
                x.RetainerName,
                x.RetainerId))
            .ToArray();

        // Dalamud emits one event for each raw market packet (up to ten listings),
        // not one event for the complete result page. Aggregate packets sharing the
        // request ID and finish after a short quiet period. This also prevents a late
        // packet from the previous same-item search becoming the next row's result.
        request.AddOfferingsPacket(offerings.RequestId, rows);
    }

    private void OnHistoryReceived(IMarketBoardHistory history)
    {
        PendingRequest? request;
        lock (sync)
            request = pending;
        if (request is null || (request.RequestedItemId != 0 && history.ItemId != request.RequestedItemId))
            return;

        request.ResolveItem(history.ItemId);
        request.History.TrySetResult(history.HistoryListings
            .Where(x => x.SalePrice > 0)
            .Select(x => x.SalePrice)
            .ToArray());
    }

    public void Dispose()
    {
        ClearCache();
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
        marketBoard.HistoryReceived -= OnHistoryReceived;
    }

    private void RememberCompletedRequest(int requestId)
    {
        if (!completedRequestIds.Add(requestId))
            return;
        completedRequestOrder.Enqueue(requestId);
        while (completedRequestOrder.Count > 8)
            completedRequestIds.Remove(completedRequestOrder.Dequeue());
    }

    private sealed class PendingRequest(uint itemId)
    {
        private static readonly TimeSpan PacketQuietPeriod = TimeSpan.FromMilliseconds(350);
        private readonly object packetSync = new();
        private readonly HashSet<MarketListing> accumulatedListings = [];
        private int packetGeneration;

        public uint RequestedItemId { get; } = itemId;
        public uint ResolvedItemId { get; private set; } = itemId;
        public int? RequestId { get; private set; }
        public TaskCompletionSource<IReadOnlyList<MarketListing>> Offerings { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<uint[]> History { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResolveItem(uint resolvedItemId)
        {
            if (resolvedItemId != 0 && ResolvedItemId == 0)
                ResolvedItemId = resolvedItemId;
        }

        public void AddOfferingsPacket(int requestId, IReadOnlyList<MarketListing> listings)
        {
            int generation;
            lock (packetSync)
            {
                if (Offerings.Task.IsCompleted)
                    return;
                if (RequestId.HasValue && RequestId.Value != requestId)
                    return;
                RequestId = requestId;
                foreach (var listing in listings)
                    accumulatedListings.Add(listing);
                generation = ++packetGeneration;
            }
            _ = CompleteAfterQuietPeriodAsync(generation);
        }

        private async Task CompleteAfterQuietPeriodAsync(int generation)
        {
            await Task.Delay(PacketQuietPeriod).ConfigureAwait(false);
            lock (packetSync)
            {
                if (generation != packetGeneration || Offerings.Task.IsCompleted)
                    return;
                Offerings.TrySetResult(accumulatedListings.ToArray());
            }
        }

        public void Cancel()
        {
            Offerings.TrySetCanceled();
            History.TrySetCanceled();
        }
    }
}
