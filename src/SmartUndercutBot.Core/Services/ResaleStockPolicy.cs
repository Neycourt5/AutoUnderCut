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
        "Grade 4 Gemdraught of Intelligence" or "Grade 4 Gemdraught of Mind" or "Caramel Popcorn";

    public static bool QualityAllowed(ProcurementRule rule, bool hq) =>
        hq ? rule.AllowHighQuality || rule.RequireHighQuality : !rule.RequireHighQuality;

    public static bool TradesHighQuality(ProcurementRule rule) =>
        rule.AllowHighQuality || rule.RequireHighQuality;

    // "Only buy high quality" means high quality wins wherever both forms exist.
    // Items that only ever exist at normal quality, such as dyes, stay buyable.
    public static bool BuyableQuality(ProcurementRule rule, bool hq, bool highQualityOnly) =>
        QualityAllowed(rule, hq) && (hq || !highQualityOnly || !TradesHighQuality(rule));

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
