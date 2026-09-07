using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class ResaleStockPolicy
{
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
}
