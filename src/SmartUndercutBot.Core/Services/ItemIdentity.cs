namespace SmartUndercutBot.Core.Services;

/// <summary>
/// The game writes item ids with quality offsets in packets and text: one million
/// for high quality, half a million for collectables. Comparing raw ids from two
/// different sources is how a rejected high-quality purchase went unnoticed - the
/// server's answer named item + 1,000,000 while the plugin waited for item.
/// </summary>
public static class ItemIdentity
{
    public const uint HighQualityOffset = 1_000_000;
    public const uint CollectableOffset = 500_000;

    /// <summary>The plain item id, or zero for event items and other non-market ids.</summary>
    public static uint BaseId(uint raw) => RetainerSaleParser.SplitItemId(raw).ItemId;

    /// <summary>Whether two ids name the same market item, whatever quality offset either carries.</summary>
    public static bool SameItem(uint first, uint second) =>
        BaseId(first) is var id and > 0 && id == BaseId(second);
}
