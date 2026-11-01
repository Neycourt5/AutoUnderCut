using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// Reads the game's own "has sold" system message. This is the one real sale
/// signal the plugin has: every completed retainer sale while the player is
/// logged in is reported through a LogMessage row of log kind 71.
///
/// The rows, read from the 7.x game data with their macro parameters:
/// <code>
/// 745: The &lt;qty&gt; &lt;item&gt; you put up for sale in the &lt;town&gt; markets have sold for &lt;gil&gt; gil (after fees).
///      lnum1 = town, lnum2 = item, lnum3 = quantity, lnum5 = gil after fees
/// 748: &lt;item&gt; you put up for sale in the &lt;town&gt; markets has sold for &lt;gil&gt; gil (after fees).
///      lnum1 = town, lnum2 = item, lnum5 = gil after fees (a single unit)
/// </code>
/// Parameters are numbered from zero, so lnum1 is index 0. Rows 6081/6082 are
/// mannequin sales and are deliberately not treated as retainer sales.
/// </summary>
public static class RetainerSaleParser
{
    public const uint StackSaleMessageId = 745;
    public const uint SingleSaleMessageId = 748;
    public const int TownParameter = 0;
    public const int ItemParameter = 1;
    public const int QuantityParameter = 2;
    public const int GilParameter = 4;

    public static bool IsSaleMessage(uint logMessageId) =>
        logMessageId is StackSaleMessageId or SingleSaleMessageId;

    /// <param name="intParameter">Reads one integer parameter, or null when it is absent or not a number.</param>
    public static RetainerSale? Parse(uint logMessageId, Func<int, int?> intParameter, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(intParameter);
        if (!IsSaleMessage(logMessageId))
            return null;
        var rawItem = intParameter(ItemParameter);
        var quantity = logMessageId == StackSaleMessageId ? intParameter(QuantityParameter) : 1;
        var gil = intParameter(GilParameter);
        if (rawItem is not > 0 || quantity is not > 0 || gil is not >= 0)
            return null;
        var (itemId, highQuality) = SplitItemId((uint)rawItem.Value);
        if (itemId == 0)
            return null;
        var town = intParameter(TownParameter) ?? 0;
        return new(at, itemId, highQuality, (uint)quantity.Value, (ulong)gil.Value, (uint)Math.Max(0, town));
    }

    /// <summary>
    /// Item ids in game text carry quality offsets: one million for high quality,
    /// half a million for collectables. Event items (two million and up) are never
    /// market goods.
    /// </summary>
    public static (uint ItemId, bool HighQuality) SplitItemId(uint raw) => raw switch
    {
        >= 2_000_000 => (0u, false),
        >= 1_000_000 => (raw - 1_000_000, true),
        >= 500_000 => (raw - 500_000, false),
        _ => (raw, false),
    };

    /// <summary>
    /// Settle the quality of a sale from our own listings when the message did not
    /// carry it: the listing with the same item and quantity whose asking price,
    /// after the seller's tax, is closest to the gil received. A match has to land
    /// within a few percent; otherwise the parsed value stands.
    /// </summary>
    public static RetainerSale ResolveQuality(RetainerSale sale,
        IEnumerable<(uint ItemId, bool IsHighQuality, uint Quantity, uint AskingUnitPrice)> listings,
        decimal sellerTaxPercent)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(listings);
        if (sale.IsHighQuality || sale.Quantity == 0)
            return sale;
        var keep = 1m - Math.Clamp(sellerTaxPercent, 0m, 50m) / 100m;
        var best = listings
            .Where(x => x.ItemId == sale.ItemId && x.Quantity == sale.Quantity && x.AskingUnitPrice > 0)
            .Select(x => (Listing: x, Error: Math.Abs(x.AskingUnitPrice * (decimal)x.Quantity * keep - sale.GilAfterFees) /
                                             Math.Max(1m, sale.GilAfterFees)))
            .Where(x => x.Error <= 0.03m)
            .OrderBy(x => x.Error)
            .FirstOrDefault();
        return best.Listing.ItemId == 0 ? sale : sale with { IsHighQuality = best.Listing.IsHighQuality };
    }
}
