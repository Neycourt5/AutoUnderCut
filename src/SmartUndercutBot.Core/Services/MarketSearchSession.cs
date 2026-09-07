namespace SmartUndercutBot.Core.Services;

public sealed record MarketSearchRow(int Index, uint ItemId, bool Enabled);
public sealed record MarketSearchResult(bool Visible, uint ItemId, bool Waiting, bool ResponseReceived = true);

public interface IMarketSearchUi
{
    // Names the exact precondition that refused the last call, so a stalled search
    // reports why on screen instead of leaving the route to guess.
    string? LastBlocker => null;
    bool PrepareSearch(string itemName);
    bool SubmitSearch(string itemName);
    IReadOnlyList<MarketSearchRow> ReadRows();
    bool ActivateRow(int index, uint itemId);
    MarketSearchResult ReadResult();
    void CloseResult();
    void ClearSearch();
}

// The route owns the overall timeout/retry budget. This session advances the
// individual UI steps without repeatedly submitting a search or clicking a row.
public sealed class MarketSearchSession(IMarketSearchUi ui, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private uint itemId;
    private string itemName = string.Empty;
    private bool submitted;
    private bool selected;
    private DateTimeOffset nextActionAt;
    public string Status { get; private set; } = "No item search is active.";

    public bool Request(uint id, string name)
    {
        if (id == 0 || string.IsNullOrWhiteSpace(name)) return false;
        if (id == itemId) return true;
        Reset();
        if (!ui.PrepareSearch(name))
        {
            Status = ui.LastBlocker is { } blocked
                ? $"Waiting for the market-board search controls: {blocked}."
                : "Waiting for the market-board search controls.";
            return false;
        }
        itemId = id;
        itemName = name;
        nextActionAt = time.GetUtcNow().AddMilliseconds(400);
        Status = $"Preparing the item search for {name}.";
        return true;
    }

    public bool Poll(uint id)
    {
        if (id == 0 || id != itemId) return false;
        if (!submitted)
        {
            if (time.GetUtcNow() < nextActionAt) return false;
            if (!ui.SubmitSearch(itemName))
            {
                Status = ui.LastBlocker is { } blocked
                    ? $"Waiting to submit the item search for {itemName}: {blocked}."
                    : $"Waiting to submit the item search for {itemName}.";
                return false;
            }
            submitted = true;
            nextActionAt = time.GetUtcNow().AddMilliseconds(500);
            Status = $"Search submitted for {itemName}; waiting for the matching item row.";
            return false;
        }

        var result = ui.ReadResult();
        if (result.Visible)
        {
            if (selected && result.ItemId == itemId)
            {
                var ready = result.ResponseReceived && !result.Waiting && time.GetUtcNow() >= nextActionAt;
                Status = ready ? $"Live prices loaded for {itemName}." : $"Waiting for live prices for {itemName}.";
                return ready;
            }
            if (!result.Waiting) ui.CloseResult();
            Status = $"Closing old item results before opening {itemName}.";
            return false;
        }
        // Only the wait for the results window itself belongs above. "Waiting" here
        // means an in-flight *listing* request, which says nothing about whether the
        // name-search rows have populated - gating on it left the search sitting at
        // "waiting for the matching item row" until it timed out, every time.
        if (time.GetUtcNow() < nextActionAt) return false;

        // A slow server may not open the results window for several seconds.
        // Keep waiting after our one click; the route owns bounded read retries.
        if (selected) return false;
        var rows = ui.ReadRows();
        var row = rows.FirstOrDefault(x => x.ItemId == itemId && x.Enabled);
        if (row is null)
        {
            Status = rows.Count == 0 ? $"Waiting for search rows for {itemName}; no results are visible yet."
                : $"Waiting for the exact {itemName} row; {rows.Count} other/disabled row(s) are visible.";
            return false;
        }
        if (!ui.ActivateRow(row.Index, itemId))
        {
            Status = $"The search row for {itemName} changed; waiting for it to stabilize.";
            nextActionAt = time.GetUtcNow().AddMilliseconds(500);
            return false;
        }
        selected = true;
        nextActionAt = time.GetUtcNow().AddSeconds(3);
        Status = $"Opened the {itemName} row; waiting for live listings.";
        return false;
    }

    public void Reset()
    {
        ui.ClearSearch();
        itemId = 0;
        itemName = string.Empty;
        submitted = selected = false;
        nextActionAt = default;
        Status = "No item search is active.";
    }
}
