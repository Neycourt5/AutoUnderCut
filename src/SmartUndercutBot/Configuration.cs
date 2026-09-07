using Dalamud.Configuration;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 29;
    public bool AutomationEnabled { get; set; }
    public bool ProcessAllRetainers { get; set; } = true;
    public bool RepeatBellRuns { get; set; }
    public int RepeatMinimumMinutes { get; set; } = 5;
    public int RepeatMaximumMinutes { get; set; } = 10;
    public bool AllowAutomaticWrites { get; set; }
    public bool OpenDashboardOnRetainer { get; set; } = true;
    public int MinimumDelayMs { get; set; } = 250;
    public int MaximumDelayMs { get; set; } = 450;
    public int MarketRequestTimeoutSeconds { get; set; } = 10;
    public int MarketRequestCooldownMs { get; set; } = 3000;
    public int MarketRequestRetryCount { get; set; } = 2;
    public int MarketRetryBackoffMs { get; set; } = 2000;
    public int MaximumUpdatesPerSession { get; set; } = 200;
    public bool AutomaticallyCollectRetainerGil { get; set; } = true;
    public bool AutomaticProcurementEnabled { get; set; }
    public bool AllowAutomaticPurchases { get; set; }
    public bool AllowAutomaticListing { get; set; }
    public bool AutomaticCuratedBagListingEnabled { get; set; } = true;
    public int BagListingReservePerItem { get; set; } = 100;
    public int ProcurementIntervalMinutes { get; set; } = 10;
    public uint ProcurementBudget { get; set; } = 5_000_000;
    public bool ReinvestAvailableGil { get; set; } = true;
    public bool BuyHighQualityOnly { get; set; } = true;
    public int ProcurementBagBufferStacks { get; set; } = 5;
    public bool ContinueShoppingWhenStocked { get; set; } = true;
    public decimal ProcurementBufferGilPercent { get; set; } = 20m;
    public uint ProcurementTravelReserve { get; set; } = 5_000;
    public decimal ProcurementWeeklySalesSharePercent { get; set; } = 25m;
    public bool DyeRulesSeeded { get; set; }
    public bool MateriaRulesSeeded { get; set; }
    public bool BuyableDyeRulesSeeded { get; set; }
    public bool TomeMaterialRulesSeeded { get; set; }
    public int ProcurementTargetSaleSlots { get; set; } = 60;
    public int ProcurementInventoryReserve { get; set; } = 10;
    public decimal ProcurementMinimumRoiPercent { get; set; } = 20m;
    public uint ProcurementMinimumProfitPerUnit { get; set; } = 100;
    public string ProcurementDataCenter { get; set; } = "North-America";
    public string MarketBoardTravelCommand { get; set; } = "/li tp Limsa Lominsa Lower Decks";
    public bool PriorityShoppingEnabled { get; set; } = true;
    public string PriorityNextWorld { get; set; } = string.Empty;
    public uint PriorityNextItem { get; set; }
    // Empty means "use the market-board command", which is the original behaviour.
    public string SummoningBellTravelCommand { get; set; } = string.Empty;
    public bool LiveWorldStockHuntEnabled { get; set; } = true;
    public int LiveWorldStockThresholdPerItem { get; set; } = 199;
    public int LiveWorldStockHuntMaximumItems { get; set; } = 8;
    public int LiveWorldStockHuntCooldownMinutes { get; set; } = 360;
    public int GuidedTourMaximumWorlds { get; set; } = 6;
    public List<ProcurementRule> ProcurementRules { get; set; } = [];
    public PricingRule GlobalRule { get; set; } = new();
    public Dictionary<uint, PricingRule> PerItemRules { get; set; } = [];

    public PricingRule GetEffectiveRule(uint itemId) =>
        PerItemRules.TryGetValue(itemId, out var rule) ? rule.Clone() : GlobalRule.Clone();

    public bool KeepsRetainersStocked => AutomationEnabled && ProcessAllRetainers && RepeatBellRuns &&
        AllowAutomaticWrites && AutomaticProcurementEnabled && AllowAutomaticPurchases &&
        AllowAutomaticListing && AutomaticCuratedBagListingEnabled && !LiveWorldStockHuntEnabled;

    public void EnableStockAutomation()
    {
        AutomationEnabled = true;
        ProcessAllRetainers = true;
        RepeatBellRuns = true;
        AllowAutomaticWrites = true;
        AutomaticProcurementEnabled = true;
        AllowAutomaticPurchases = true;
        AllowAutomaticListing = true;
        AutomaticCuratedBagListingEnabled = true;
        AutomaticallyCollectRetainerGil = true;
        // Use targeted routes for recurring refills. Full live tours remain a manual option.
        LiveWorldStockHuntEnabled = false;
    }

    public void DisableStockAutomation()
    {
        AutomationEnabled = false;
        RepeatBellRuns = false;
        AutomaticProcurementEnabled = false;
        AutomaticCuratedBagListingEnabled = false;
        AllowAutomaticWrites = false;
        AllowAutomaticPurchases = false;
        AllowAutomaticListing = false;
    }

    public void Normalize()
    {
        if (Version < 2)
        {
            // Version 1 shipped with tolerance bands that made a one-gil undercut look idle.
            // Migrate untouched defaults to the requested always-undercut behavior.
            if (GlobalRule is not null && GlobalRule.AbsoluteTolerance == 5 && GlobalRule.PercentageTolerance == 0.5m)
            {
                GlobalRule.AbsoluteTolerance = 0;
                GlobalRule.PercentageTolerance = 0;
            }
            if (MaximumUpdatesPerSession == 20)
                MaximumUpdatesPerSession = 200;
            Version = 2;
        }
        if (Version < 3)
        {
            if (MinimumDelayMs == 800 && MaximumDelayMs == 1500)
            {
                MinimumDelayMs = 250;
                MaximumDelayMs = 450;
            }
            Version = 3;
        }
        if (Version < 4)
        {
            if (MinimumDelayMs == 250 && MaximumDelayMs == 1500)
                MaximumDelayMs = 450;
            Version = 4;
        }
        if (Version < 5)
        {
            // Version 4 could crash the client while confirming Adjust Price. Require
            // the user to explicitly re-arm writes after installing the safe callback fix.
            AllowAutomaticWrites = false;
            Version = 5;
        }
        if (Version < 6)
        {
            ProcessAllRetainers = true;
            if (MarketRequestCooldownMs == 0)
                MarketRequestCooldownMs = 1200;
            Version = 6;
        }
        if (Version < 7)
        {
            if (RepeatMinimumMinutes == 0)
                RepeatMinimumMinutes = 5;
            if (RepeatMaximumMinutes == 0)
                RepeatMaximumMinutes = 10;
            Version = 7;
        }
        if (Version < 8)
        {
            // Market requests now have their own throttle. Undo the old workaround that
            // slowed every unrelated UI action to 1200 ms.
            if (MinimumDelayMs == 1200 && MaximumDelayMs == 1200)
            {
                MinimumDelayMs = 250;
                MaximumDelayMs = 450;
            }
            if (RepeatMinimumMinutes == 5 && RepeatMaximumMinutes == 5)
                RepeatMaximumMinutes = 10;
            Version = 8;
        }
        if (Version < 9)
        {
            // Procurement is deliberately opt-in and starts disarmed.
            AutomaticProcurementEnabled = false;
            AllowAutomaticPurchases = false;
            AllowAutomaticListing = false;
            Version = 9;
        }
        if (Version < 10)
        {
            // The original 20% guard treated ordinary market movement (for example,
            // 5,696 down to roughly 4,200) as a price war and never reached the write
            // step. Preserve protection only for genuinely extreme collapses.
            if (GlobalRule is not null && GlobalRule.PriceWarDropPercent == 20m)
                GlobalRule.PriceWarDropPercent = 60m;
            if (PerItemRules is not null)
            {
                foreach (var rule in PerItemRules.Values.Where(x => x.PriceWarDropPercent == 20m))
                    rule.PriceWarDropPercent = 60m;
            }
            Version = 10;
        }
        if (Version < 11)
        {
            // Repeated Compare Prices requests can be rejected by the game even when
            // the previous request completed. Give distinct items more room and retry
            // transient failures instead of silently abandoning the listing.
            if (MarketRequestCooldownMs <= 1200)
                MarketRequestCooldownMs = 1600;
            if (MarketRequestRetryCount == 0)
                MarketRequestRetryCount = 2;
            if (MarketRetryBackoffMs == 0)
                MarketRetryBackoffMs = 2000;
            Version = 11;
        }
        if (Version < 12)
        {
            ProcurementRules ??= [];
            var removedDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Grade 3 Gemdraught of Strength",
                "Grade 3 Gemdraught of Dexterity",
                "Grade 3 Gemdraught of Intelligence",
                "Grade 3 Gemdraught of Mind",
                "Mate Cookie",
                "Mollete",
                "Popoto Potage",
            };
            ProcurementRules.RemoveAll(x => removedDefaults.Contains(x.ItemName));
            foreach (var rule in ProcurementRules.Where(x =>
                         string.Equals(x.ItemName, "Caramel Popcorn", StringComparison.OrdinalIgnoreCase) ||
                         x.ItemName?.StartsWith("Grade 4 Gemdraught of ", StringComparison.OrdinalIgnoreCase) == true))
            {
                rule.AllowHighQuality = true;
                rule.RequireHighQuality = true;
            }
            if (ProcurementIntervalMinutes == 30)
                ProcurementIntervalMinutes = 10;
            Version = 12;
        }
        if (Version < 13)
        {
            if (BagListingReservePerItem == 0)
                BagListingReservePerItem = 100;
            AutomaticCuratedBagListingEnabled = true;
            Version = 13;
        }
        if (Version < 14)
        {
            if (string.IsNullOrWhiteSpace(ProcurementDataCenter) ||
                string.Equals(ProcurementDataCenter.Trim(), "Aether", StringComparison.OrdinalIgnoreCase))
                ProcurementDataCenter = "North-America,Oceania";
            Version = 14;
        }
        if (Version < 15)
        {
            LiveWorldStockHuntEnabled = true;
            if (LiveWorldStockThresholdPerItem == 0)
                LiveWorldStockThresholdPerItem = 199;
            if (LiveWorldStockHuntCooldownMinutes == 0)
                LiveWorldStockHuntCooldownMinutes = 360;
            Version = 15;
        }
        if (Version < 16)
        {
            if (GuidedTourMaximumWorlds == 0)
                GuidedTourMaximumWorlds = 6;
            // Automatic purchase interaction has not been reliable across live
            // client revisions. Disarm it on upgrade; the guided Universalis
            // route remains available without this switch.
            AllowAutomaticPurchases = false;
            LiveWorldStockHuntEnabled = false;
            Version = 16;
        }
        if (Version < 17)
        {
            AutomaticallyCollectRetainerGil = true;
            Version = 17;
        }
        if (Version < 18)
        {
            ReinvestAvailableGil = true;
            Version = 18;
        }
        if (Version < 19)
        {
            // Dyes were briefly seeded as buyable stock. The user keeps no dye or
            // materia inventory, so re-seed both as sell-only on the next start.
            DyeRulesSeeded = false;
            MateriaRulesSeeded = false;
            foreach (var rule in ProcurementRules.Where(x =>
                         x.ItemName.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase)))
            {
                rule.LiquidateOnly = true;
                rule.ListFromBags = true;
                rule.BagReserveQuantity = 0;
            }
            Version = 19;
        }
        if (Version < 20)
        {
            // Ethers joined the sell-off list and the per-item slot cap was raised,
            // so re-seed. Existing sell-only rules are refreshed, not duplicated.
            DyeRulesSeeded = false;
            MateriaRulesSeeded = false;
            Version = 20;
        }
        if (Version < 21)
        {
            // Normal-quality resale stock does not sell for this player.
            BuyHighQualityOnly = true;
            Version = 21;
        }
        if (Version < 22)
        {
            ProcurementBagBufferStacks = 5;
            Version = 22;
        }
        if (Version < 23)
        {
            // High-volume dye lines move from the sell-off list to tradeable stock.
            DyeRulesSeeded = false;
            BuyableDyeRulesSeeded = false;
            ProcurementRules.RemoveAll(x => x.LiquidateOnly &&
                x.ItemName.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase) &&
                (x.ItemName.StartsWith("General-Purpose ", StringComparison.OrdinalIgnoreCase) ||
                 x.ItemName.StartsWith("Wide-Spectrum ", StringComparison.OrdinalIgnoreCase)));
            Version = 23;
        }
        if (Version < 26)
        {
            // The all-world tour becomes an explicit allowlist: raid food and
            // potions first, then the mass-market dye lines, then current materia.
            // Re-seed so the tour flags and the grade XI/XII materia rules exist.
            DyeRulesSeeded = false;
            MateriaRulesSeeded = false;
            BuyableDyeRulesSeeded = false;
            ProcurementRules.RemoveAll(x => x.LiquidateOnly &&
                ResaleStockPolicy.IsTradeableMateria(x.ItemName));
            Version = 26;
        }
        if (Version < 27)
        {
            // Tomestone materials are listed from the bags, never bought.
            TomeMaterialRulesSeeded = false;
            Version = 27;
        }
        if (Version < 28)
        {
            // 1.6s between market-board queries is fast enough that the game starts
            // dropping them silently a few rows into a pass.
            if (MarketRequestCooldownMs < 3000)
                MarketRequestCooldownMs = 3000;
            Version = 28;
        }
        if (Version < 29)
        {
            if (string.Equals(MarketBoardTravelCommand?.Trim(), "/li mb", StringComparison.OrdinalIgnoreCase))
                MarketBoardTravelCommand = "/li tp Limsa Lominsa Lower Decks";
            Version = 29;
        }
        Version = Math.Max(Version, 29);
        LiveWorldStockHuntMaximumItems = Math.Clamp(LiveWorldStockHuntMaximumItems, 1, 40);
        ProcurementBufferGilPercent = Math.Clamp(ProcurementBufferGilPercent, 0m, 100m);
        ProcurementBagBufferStacks = Math.Clamp(ProcurementBagBufferStacks, 0, 50);
        ProcurementTravelReserve = Math.Min(ProcurementTravelReserve, 100_000_000u);
        ProcurementWeeklySalesSharePercent = Math.Clamp(ProcurementWeeklySalesSharePercent, 1m, 100m);
        MinimumDelayMs = Math.Clamp(MinimumDelayMs, 100, 60_000);
        MaximumDelayMs = Math.Clamp(MaximumDelayMs, MinimumDelayMs, 60_000);
        MarketRequestTimeoutSeconds = Math.Clamp(MarketRequestTimeoutSeconds, 2, 60);
        MarketRequestCooldownMs = Math.Clamp(MarketRequestCooldownMs, 1000, 10_000);
        MarketRequestRetryCount = Math.Clamp(MarketRequestRetryCount, 0, 5);
        MarketRetryBackoffMs = Math.Clamp(MarketRetryBackoffMs, 1000, 10_000);
        RepeatMinimumMinutes = Math.Clamp(RepeatMinimumMinutes, 5, 1_440);
        RepeatMaximumMinutes = Math.Clamp(RepeatMaximumMinutes, RepeatMinimumMinutes, 1_440);
        MaximumUpdatesPerSession = Math.Clamp(MaximumUpdatesPerSession, 1, 200);
        ProcurementIntervalMinutes = Math.Clamp(ProcurementIntervalMinutes, 5, 1_440);
        ProcurementBudget = Math.Clamp(ProcurementBudget, 1_000u, 100_000_000u);
        ProcurementTargetSaleSlots = Math.Clamp(ProcurementTargetSaleSlots, 1, 200);
        ProcurementInventoryReserve = Math.Clamp(ProcurementInventoryReserve, 1, 100);
        LiveWorldStockThresholdPerItem = Math.Clamp(LiveWorldStockThresholdPerItem, 1, 9999);
        LiveWorldStockHuntCooldownMinutes = Math.Clamp(LiveWorldStockHuntCooldownMinutes, 60, 10_080);
        GuidedTourMaximumWorlds = Math.Clamp(GuidedTourMaximumWorlds, 1, 20);
        BagListingReservePerItem = Math.Clamp(BagListingReservePerItem, 0, 9999);
        ProcurementMinimumRoiPercent = Math.Clamp(ProcurementMinimumRoiPercent, 0m, 1_000m);
        ProcurementMinimumProfitPerUnit = Math.Clamp(ProcurementMinimumProfitPerUnit, 0u, 100_000_000u);
        ProcurementDataCenter = ProcurementTravelPolicy.ShoppingScope(ProcurementDataCenter);
        SummoningBellTravelCommand = SummoningBellTravelCommand?.Trim() ?? string.Empty;
        MarketBoardTravelCommand = string.IsNullOrWhiteSpace(MarketBoardTravelCommand)
            ? "/li tp Limsa Lominsa Lower Decks"
            : MarketBoardTravelCommand.Trim();
        ProcurementRules ??= [];
        foreach (var rule in ProcurementRules)
        {
            rule.ItemName ??= string.Empty;
            rule.TargetStackSize = Math.Clamp(rule.TargetStackSize, 1, 999);
            rule.MaximumSaleSlots = Math.Clamp(rule.MaximumSaleSlots, 1, 60);
            rule.MinimumWeeklyUnitsSold = Math.Clamp(rule.MinimumWeeklyUnitsSold, 0, 1_000_000);
            if (rule.RequireHighQuality)
                rule.AllowHighQuality = true;
        }
        GlobalRule ??= new PricingRule();
        PerItemRules ??= [];
    }
}
