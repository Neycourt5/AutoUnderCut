using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class ResaleStockPolicy
{
    // Roughly one fifth of a full retainer portfolio ready to replace sales.
    public static int ComfortableBagTarget(int totalSaleSlots) =>
        Math.Clamp((Math.Max(0, totalSaleSlots) + 4) / 5, 5, 20);

    public static int ComfortableItemTarget(int listedSlots) =>
        Math.Clamp((Math.Max(0, listedSlots) + 3) / 4, 1, 3);

    public static bool IsCuratedConsumable(string name) => name is
        "Grade 4 Gemdraught of Strength" or "Grade 4 Gemdraught of Dexterity" or
        "Grade 4 Gemdraught of Intelligence" or "Grade 4 Gemdraught of Mind" or
        "Caramel Popcorn" or "Popoto Potage";

    /// <summary>
    /// The high-volume dye lines worth trading rather than selling off. The game
    /// writes "General-purpose ... Dye" and "Wide Spectrum #1 Dye"; a seeder that
    /// only knew "Wide-Spectrum" filed both Wide Spectrum dyes as sell-off stock.
    /// </summary>
    public static bool IsMassMarketDye(string name) =>
        name.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("General-purpose ", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("General purpose ", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("Wide-Spectrum ", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("Wide Spectrum ", StringComparison.OrdinalIgnoreCase));

    // Materials bought with tomestones. They are acquired for gil, never traded for,
    // so they are listed from the bags with nothing held back and never purchased.
    public static readonly string[] TomeMaterialNames =
    [
        "Diatryma Pelt", "Hydrophobic Preservative", "Double Duracoat", "Everkeep Resin",
        "Turali Pigment", "Mastodon Pelt", "Shaaloani Coke", "Yollal Extract",
    ];

    public static bool IsTomeMaterial(string name) =>
        TomeMaterialNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    // Only grades XI and XII are current enough to be worth trading; every other
    // grade stays on the sell-off list.
    public static bool IsTradeableMateria(string name) =>
        name.EndsWith(" Materia XI", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(" Materia XII", StringComparison.OrdinalIgnoreCase);

    // Walk the tour in the order the stock is worth having: raid food and potions
    // first, then the mass-market dyes, then current materia last.
    public static IReadOnlyList<ProcurementRule> SelectTourRules(
        IEnumerable<ProcurementRule> rules, Func<ProcurementRule, bool> isBelowTarget, int maximumItems) =>
        rules.Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly && x.HuntOnTour)
            .Where(isBelowTarget)
            .DistinctBy(x => x.ItemId)
            .OrderBy(x => x.TourPriority)
            .ThenBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maximumItems))
            .ToArray();

    // A stack count alone is a poor measure of stock health: twelve stacks of cheap
    // dye is not the same trading position as twelve stacks of gemdraughts. Stock is
    // only "comfortable" when the buffer meets both the stack target and a value
    // target, so shopping keeps going while the bags are full of cheap goods.
    public static bool BufferIsComfortable(int stacks, int stackTarget, ulong value, ulong valueTarget) =>
        stacks >= Math.Max(0, stackTarget) && value >= valueTarget;

    /// <summary>How short of the value target the buffer is, for display and logging.</summary>
    public static ulong BufferValueShortfall(ulong value, ulong valueTarget) =>
        value >= valueTarget ? 0 : valueTarget - value;

    public static bool QualityAllowed(ProcurementRule rule, bool hq) =>
        hq ? rule.AllowHighQuality || rule.RequireHighQuality : !rule.RequireHighQuality;

    public static bool TradesHighQuality(ProcurementRule rule) =>
        rule.AllowHighQuality || rule.RequireHighQuality;

    // "Only buy high quality" means high quality wins wherever both forms exist.
    // Items that only ever exist at normal quality, such as dyes, stay buyable.
    public static bool BuyableQuality(ProcurementRule rule, bool hq, bool highQualityOnly) =>
        QualityAllowed(rule, hq) && (hq || !highQualityOnly || !TradesHighQuality(rule));

    /// <summary>
    /// The least a listing of a line sized to its buyers may be worth. Buyers take
    /// whole listings, so a listing only as big as one typical purchase sells
    /// soonest - but a forty-thousand-gil listing is a wasted retainer slot.
    /// </summary>
    public const uint MinimumListingValue = 100_000;

    /// <summary>
    /// Units per retainer listing: the rule's own listing size when it has one,
    /// whole 99-stacks for the curated consumables, otherwise the purchase lot.
    /// </summary>
    public static int ListingSize(ProcurementRule? rule, string name) =>
        rule?.ListingStackSize is > 0 and var size ? Math.Min(size, 999)
        : IsCuratedConsumable(name) ? 99
        : Math.Max(1, rule?.TargetStackSize ?? 1);

    /// <summary>
    /// The listing size at this price, raised until one listing is worth
    /// <see cref="MinimumListingValue"/>. Only lines with an explicit listing size
    /// are sized to their buyers; everything else keeps the size it always had.
    /// </summary>
    public static int ListingQuantity(ProcurementRule? rule, string name, uint unitPrice)
    {
        var size = ListingSize(rule, name);
        if (rule?.ListingStackSize is not > 0 || unitPrice == 0)
            return size;
        var needed = (int)Math.Min(999u, (MinimumListingValue + unitPrice - 1) / unitPrice);
        return Math.Clamp(Math.Max(size, needed), 1, 999);
    }

    public static bool CanListFromBags(ProcurementRule? rule, string name, bool hq) =>
        rule?.Enabled != false && (rule is null || QualityAllowed(rule, hq)) &&
        (rule?.ListFromBags ?? (hq && IsCuratedConsumable(name)));

    public static uint BagReserve(ProcurementRule? rule, string name, uint consumableReserve) =>
        rule?.BagReserveQuantity ?? (IsCuratedConsumable(name) ? consumableReserve : 0);

    public static uint SpendableGil(uint wallet, uint travelReserve, bool reinvest, uint tripCap, uint spent = 0)
    {
        var available = wallet > travelReserve ? wallet - travelReserve : 0;
        return reinvest ? available : Math.Min(available, tripCap > spent ? tripCap - spent : 0);
    }

    // Value existing buffer stock at its saved acquisition cost. Buying moves gil
    // from the wallet into this exposure, so repeated trips cannot reset the cap.
    public static uint BufferSpendableGil(uint walletAfterReserve, ulong bufferCost, decimal percent)
    {
        var ceiling = decimal.Floor(((decimal)walletAfterReserve + bufferCost) * Math.Clamp(percent, 0m, 100m) / 100m);
        return (uint)Math.Clamp(ceiling - bufferCost, 0m, walletAfterReserve);
    }
}
