namespace SmartUndercutBot.Core.Services;

public static class MarketSearchRowResolver
{
    private const int MaximumRows = 100;

    public static MarketSearchRow? FindExact(
        uint expectedItemId,
        string expectedItemName,
        string currentSearchText,
        int renderedRowCount,
        IReadOnlyList<uint> transientItemIds,
        IReadOnlyList<uint> listingPageItemIds,
        IReadOnlyList<bool> disabledRows,
        bool searchSettled,
        bool normalMode)
    {
        if (expectedItemId == 0 || string.IsNullOrWhiteSpace(expectedItemName) ||
            renderedRowCount <= 0 || transientItemIds is null ||
            listingPageItemIds is null || disabledRows is null)
            return null;

        if (!searchSettled)
            return null;

        var rowCount = Math.Min(renderedRowCount, MaximumRows);
        if (listingPageItemIds.Any(id => id != 0))
            return FindById(expectedItemId, rowCount, listingPageItemIds, disabledRows);

        if (transientItemIds.Any(id => id != 0))
            return FindById(expectedItemId, rowCount, transientItemIds, disabledRows);

        if (renderedRowCount != 1 || !normalMode ||
            !string.Equals(currentSearchText, expectedItemName, StringComparison.Ordinal) ||
            !IsEnabled(0, disabledRows))
            return null;

        return new MarketSearchRow(0, expectedItemId, true);
    }

    private static MarketSearchRow? FindById(
        uint expectedItemId,
        int renderedRowCount,
        IReadOnlyList<uint> itemIds,
        IReadOnlyList<bool> disabledRows)
    {
        var count = Math.Min(renderedRowCount, itemIds.Count);
        for (var index = 0; index < count; index++)
            if (itemIds[index] == expectedItemId && IsEnabled(index, disabledRows))
                return new MarketSearchRow(index, expectedItemId, true);

        return null;
    }

    private static bool IsEnabled(int index, IReadOnlyList<bool> disabledRows) =>
        index >= 0 && index < disabledRows.Count && !disabledRows[index];
}
