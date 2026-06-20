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

            return new MarketSnapshot(itemId, DateTimeOffset.UtcNow, listings, median);
        }
        finally
        {
            lock (sync)
            {
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
            request = pending;
        if (request is null)
            return;

        var rows = offerings.ItemListings
            .Where(x => x.ItemId == request.ItemId)
            .Select(x => new MarketListing(
                x.PricePerUnit,
                x.ItemQuantity,
                x.IsHq,
                x.RetainerName,
                x.RetainerId))
            .ToArray();

        // The game sends the cheapest page first, which is all the strategy needs.
        // An empty page is still a valid "no listings" result for our one active request.
        if (rows.Length > 0 || offerings.ItemListings.Count == 0)
            request.Offerings.TrySetResult(rows);
    }

    private void OnHistoryReceived(IMarketBoardHistory history)
    {
        PendingRequest? request;
        lock (sync)
            request = pending;
        if (request is null || history.ItemId != request.ItemId)
            return;

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

    private sealed class PendingRequest(uint itemId)
    {
        public uint ItemId { get; } = itemId;
        public TaskCompletionSource<IReadOnlyList<MarketListing>> Offerings { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<uint[]> History { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            Offerings.TrySetCanceled();
            History.TrySetCanceled();
        }
    }
}
