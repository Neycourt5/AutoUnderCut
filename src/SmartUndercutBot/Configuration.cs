using Dalamud.Configuration;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 43;
    public bool AutomationEnabled { get; set; }
    public bool ProcessAllRetainers { get; set; } = true;
    public bool RepeatBellRuns { get; set; }
    public int RepeatMinimumMinutes { get; set; } = 5;
    public int RepeatMaximumMinutes { get; set; } = 10;
    public bool AllowAutomaticWrites { get; set; }
    public bool OpenDashboardOnRetainer { get; set; } = true;
    public int MinimumDelayMs { get; set; } = 250;
    public int MaximumDelayMs { get; set; } = 450;
    public int MarketRequestTimeoutSeconds { get; set; } = 6;
    public int MarketRequestCooldownMs { get; set; } = 3000;
    public int MarketRequestRetryCount { get; set; } = 2;
    public int MarketRetryBackoffMs { get; set; } = 1200;
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
    // Stock health is value as well as spread: a bag full of cheap dye meets the
    // stack target without being worth anything to sell.
    public uint ProcurementBufferValueTarget { get; set; } = 1_000_000;
    // Filling a sale slot is not the objective; holding a good portfolio is. The
    // lower fill margin only ever applies to preferred and high-liquidity stock.
    public decimal ProcurementFillRoiPercent { get; set; } = 10m;
    /// <summary>
    /// The margin required on preferred stock that is both fast-moving and valuable -
    /// popcorn, potages, raid gemdraughts. These are back out of the bags within a
    /// day or two, so the return comes from turning the gil over repeatedly rather
    /// than from the size of each flip, and holding out for a fat margin on them
    /// mostly leaves millions of gil idle.
    /// </summary>
    public decimal ProcurementFastMoverRoiPercent { get; set; } = 10m;

    // --- Demand-based inventory sizing -------------------------------------------
    // How much stock to hold, measured in days of each market's own observed sales.
    // This is what lets an exceptional high-volume line absorb real capital while
    // stopping a slow line from accumulating dead inventory, whatever its ROI.
    public decimal ProcurementPreferredCoverageDays { get; set; } = 3m;
    public decimal ProcurementSecondaryCoverageDays { get; set; } = 1.5m;
    public decimal ProcurementOpportunisticCoverageDays { get; set; } = 0.5m;
    // Stacks rarely land exactly on target. Buying is only allowed while holdings are
    // below target and may overshoot by at most this much, so one stack can complete
    // a position but a second cannot pile on top of it.
    public decimal ProcurementCoverageOvershootDays { get; set; } = 1m;

    // --- What counts as a high-volume market -------------------------------------
    // Velocity alone does not earn the thin margin: a cheap item that sells quickly
    // is not the same capital proposition as a two-million-gil stack of raid food.
    public decimal ProcurementHighVolumeMinimumSalesPerDay { get; set; } = 10m;
    public uint ProcurementHighVolumeMinimumValuePerSlot { get; set; } = 150_000;
    // Margin bar for non-preferred high-volume stock, and the deliberately stricter
    // bar for low-value stock, which is welcome as a side profit but not as a
    // destination for capital.
    public decimal ProcurementHighVolumeRoiPercent { get; set; } = 14m;
    public decimal ProcurementLowValueRoiPercent { get; set; } = 35m;
    // Nothing is ever bought below this net return, whatever the other bars say.
    public decimal ProcurementAbsoluteMinimumRoiPercent { get; set; } = 8m;
    // Days of demand worth of cheap competing stock the market swallows before it
    // should move our resale anchor or provoke an undercut.
    public decimal ProcurementAnchorAbsorptionDays { get; set; } = 0.5m;
    // Hard concentration limit no amount of demand may exceed.
    public int ProcurementEmergencyMaximumSlotsPerItem { get; set; } = 20;

    /// <summary>
    /// The retainer sale tax the game itself last reported, in percent. Every
    /// margin, ceiling, ROI figure and resale floor is computed from this, so the
    /// numbers follow the real rate rather than an assumed one. Falls back to the
    /// standard 5% until the retainer sell window has actually been read.
    /// </summary>
    public decimal ObservedMarketTaxPercent { get; set; } = 5m;
    // Portfolio shape. Preferred (core) stock should occupy most of the retainers;
    // opportunistic arbitrage is capped so it cannot crowd out capital or slots.
    public decimal PreferredPortfolioTargetPercent { get; set; } = 75m;
    public decimal OpportunisticPortfolioMaximumPercent { get; set; } = 10m;
    // A whole retainer slot is the scarce resource. A deal that cannot clear this
    // much profit is not worth occupying one, however good its ROI percentage is.
    public uint ProcurementMinimumProfitPerSaleSlot { get; set; } = 2_500;
    // Optional external market discovery. Recommendations only: every purchase
    // still needs a fresh live board reading and all existing safety checks.
    // Off by default: it downloads a whole-region dataset and is the only part of
    // the portfolio work that adds runtime behaviour outside the planner, so it
    // stays opt-in until the live market-board search is confirmed healthy.
    public bool MarketDiscoveryEnabled { get; set; }
    public int MarketDiscoveryCacheHours { get; set; } = 24;
    public string MarketDiscoveryRegion { get; set; } = "NA";
    // One circuit covers every away world. Cross-data-center travel is the
    // expensive part of a trip, so a thorough sweep beats four shallow ones; the
    // route still returns early when the bags or the wallet run out.
    public int PriorityWorldsPerTrip { get; set; } = 31;
    public int PriorityMinutesPerTrip { get; set; } = 180;
    // The same freshness limit applies to reusing a quote and approving a buy.
    public int HomePriceMaxAgeMinutes { get; set; } = 30;
    // The home price is the resale anchor, so it is kept fresh - and a retainer pass
    // re-reads it for free anyway. Away-world observations only decide where to look
    // and what to compare, and those barely move within a day.
    public int ScoutKnowledgeMaxAgeHours { get; set; } = 24;
    // A shopping trip costs 15-45 minutes away from the retainers, so it is only
    // worth taking when there is real shelf space to fill. Below this many free
    // sale slots the bot stays home and keeps undercutting instead; the repricing
    // loop is what frees the slots in the first place. Zero disables the hold.
    public int ShoppingTripMinimumFreeSaleSlots { get; set; } = 10;
    // Travelling with pocket change buys one cheap stack and wastes the trip. Hold
    // until there is a real war chest to shop with. Zero disables the hold.
    public uint ShoppingTripMinimumGil { get; set; } = 1_000_000;
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
    public List<string> PriorityScoutRoute { get; set; } = [];
    // Eight of these are the reserved snipe block - six consumables and the two
    // rare dyes - so the rest is what is left for the busiest lines and the
    // rotation. Eight per stop left none at all.
    public int PriorityItemsPerWorld { get; set; } = 14;
    // Empty means "use the market-board command", which is the original behaviour.
    public string SummoningBellTravelCommand { get; set; } = string.Empty;
    public bool LiveWorldStockHuntEnabled { get; set; } = true;
    public int LiveWorldStockThresholdPerItem { get; set; } = 199;
    public int LiveWorldStockHuntMaximumItems { get; set; } = 8;
    public int LiveWorldStockHuntCooldownMinutes { get; set; } = 360;
    public int GuidedTourMaximumWorlds { get; set; } = 6;
    public List<ProcurementRule> ProcurementRules { get; set; } = [];
    public PricingRule GlobalRule { get; set; } = new();
    /// <summary>
    /// Reserved experimental observations per "itemId:H|N". Runtime collection and
    /// planner consumption are disabled; retained for configuration compatibility. See PROFIT_OPTIMIZATION_IMPLEMENTATION.md for why
    /// the correction is deferred rather than applied.
    /// </summary>
    public Dictionary<string, SellThroughObservation> SellThrough { get; set; } = [];
    public Dictionary<uint, PricingRule> PerItemRules { get; set; } = [];

    public PricingRule GetEffectiveRule(uint itemId) =>
        PerItemRules.TryGetValue(itemId, out var rule) ? rule.Clone() : GlobalRule.Clone();

    /// <summary>
    /// The one transaction-cost model. Planning, the displayed ROI, the live
    /// pre-purchase re-check and the resale floor all read it, so the bot cannot
    /// enforce one margin and report another.
    /// </summary>
    public FeeModel Fees => FeeModel.Default with { MarketTaxPercent = ObservedMarketTaxPercent };

    /// <summary>The portfolio shape every purchase plan is built against.</summary>
    public ProcurementEconomicPolicy EconomicPolicy => new(
        PreferredCoverageDays: ProcurementPreferredCoverageDays,
        SecondaryCoverageDays: ProcurementSecondaryCoverageDays,
        OpportunisticCoverageDays: ProcurementOpportunisticCoverageDays,
        CoverageOvershootDays: ProcurementCoverageOvershootDays,
        HighVolumeMinimumSalesPerDay: ProcurementHighVolumeMinimumSalesPerDay,
        HighVolumeMinimumValuePerSlot: ProcurementHighVolumeMinimumValuePerSlot,
        CoreHighVolumeRoiPercent: ProcurementFastMoverRoiPercent,
        HighVolumeRoiPercent: ProcurementHighVolumeRoiPercent,
        StandardRoiPercent: ProcurementMinimumRoiPercent,
        LowValueRoiPercent: ProcurementLowValueRoiPercent,
        AbsoluteMinimumRoiPercent: ProcurementAbsoluteMinimumRoiPercent,
        EmergencyMaximumSlotsPerItem: ProcurementEmergencyMaximumSlotsPerItem,
        AnchorAbsorptionDays: ProcurementAnchorAbsorptionDays);

    public PortfolioGates PortfolioGates => new(
        PreferredPortfolioTargetPercent, OpportunisticPortfolioMaximumPercent, ProcurementMinimumProfitPerSaleSlot);

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

    // "General-purpose" and "Wide-Spectrum" are the high-volume lines; both are
    // written with and without a hyphen in the game's own text.
    // Rare dyes whose away-world listings are occasionally far below the home
    // price. Low volume, so the rotation reaches them seldom; worth pricing every
    // stop precisely because the good listings are rare and disappear fast.
    public static bool IsSnipeDyeName(string name) =>
        name.Contains("Jet Black Dye", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Pure White Dye", StringComparison.OrdinalIgnoreCase);

    private static bool IsMassMarketDyeName(string name) =>
        name.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("General-purpose ", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("General purpose ", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("Wide-Spectrum ", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("Wide Spectrum ", StringComparison.OrdinalIgnoreCase));

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
        if (Version < 30)
        {
            // A live response arrives in about a second when the board answers at all,
            // so a ten second wait per attempt only made a stalled row expensive.
            if (MarketRequestTimeoutSeconds > 6)
                MarketRequestTimeoutSeconds = 6;
            if (MarketRetryBackoffMs > 1200)
                MarketRetryBackoffMs = 1200;
            Version = 30;
        }
        if (Version < 31)
        {
            // The tour flags were added in v1.0.0.38 but only ever set on newly seeded
            // rules. An existing config therefore kept every gemdraught, popcorn and
            // dye at the default priority and off the tour, so the tour walked only
            // materia and skipped the items actually being flipped. Backfill them in
            // the intended order: raid food and potions, then the mass-market dyes,
            // then current materia.
            foreach (var rule in ProcurementRules.Where(x => x.ItemId != 0 && !x.LiquidateOnly))
            {
                if (ResaleStockPolicy.IsCuratedConsumable(rule.ItemName))
                    (rule.HuntOnTour, rule.TourPriority) = (true, 0);
                else if (IsMassMarketDyeName(rule.ItemName))
                    (rule.HuntOnTour, rule.TourPriority) = (true, 1);
                else if (ResaleStockPolicy.IsTradeableMateria(rule.ItemName))
                    (rule.HuntOnTour, rule.TourPriority) = (true, 2);
            }
            Version = 31;
        }
        if (Version < 32)
        {
            ProcurementBufferValueTarget = 1_000_000;
            PriorityWorldsPerTrip = 8;
            PriorityMinutesPerTrip = 45;
            Version = 32;
        }
        if (Version < 34)
        {
            ProcurementFillRoiPercent = 10m;
            Version = 34;
        }
        if (Version < 35)
        {
            ScoutKnowledgeMaxAgeHours = 24;
            Version = 35;
        }
        if (Version < 36)
        {
            // The objective changed from "fill every sale slot" to "hold a good
            // portfolio". Pin the curated consumables as preferred stock so the
            // core target means something on an existing configuration, and adopt
            // the new portfolio shape and slot-value gate.
            PreferredPortfolioTargetPercent = 75m;
            OpportunisticPortfolioMaximumPercent = 10m;
            ProcurementMinimumProfitPerSaleSlot = 2_500;
            MarketDiscoveryEnabled = true;
            MarketDiscoveryCacheHours = 24;
            foreach (var rule in ProcurementRules.Where(x =>
                         x.ItemId != 0 && !x.LiquidateOnly && ResaleStockPolicy.IsCuratedConsumable(x.ItemName)))
                rule.PreferredStock = true;
            Version = 36;
        }
        if (Version < 37)
        {
            // 1.0.0.58 shipped discovery on, which downloads a whole-region dataset
            // on the first scan after every load. A live market-board search failure
            // was reported on that build, so the new external call is disarmed and
            // its suggestions are retired until it is switched back on deliberately.
            MarketDiscoveryEnabled = false;
            ProcurementRules.RemoveAll(x => x.DiscoveredAutomatically);
            Version = 37;
        }
        if (Version < 38)
        {
            // Food and potions are now checked on every world. At eight items per
            // stop the block would leave almost no room for the rotating lines, so
            // widen a config still sitting on the old default.
            if (PriorityItemsPerWorld == 8)
                PriorityItemsPerWorld = 12;
            Version = 38;
        }
        if (Version < 39)
        {
            // Undercutting every few minutes keeps stock moving, so a trip taken to
            // fill one or two slots is mostly travel time. Wait for a worthwhile
            // batch of vacancies instead.
            ShoppingTripMinimumFreeSaleSlots = 10;
            ShoppingTripMinimumGil = 1_000_000;
            Version = 39;
        }
        if (Version < 40)
        {
            // Price the lines worth sniping on every world instead of waiting for
            // the rotation, and walk the whole circuit rather than two stops per
            // data center, so a deal on a far world is actually found.
            foreach (var rule in ProcurementRules.Where(x => x.ItemId != 0 && !x.LiquidateOnly))
                if (rule.PreferredStock || ResaleStockPolicy.IsCuratedConsumable(rule.ItemName) ||
                    IsSnipeDyeName(rule.ItemName))
                    (rule.AlwaysScout, rule.HuntOnTour) = (true, true);
            if (PriorityItemsPerWorld == 12)
                PriorityItemsPerWorld = 14;
            if (PriorityWorldsPerTrip == 8)
                PriorityWorldsPerTrip = 31;
            if (PriorityMinutesPerTrip == 45)
                PriorityMinutesPerTrip = 180;
            Version = 40;
        }
        if (Version < 41)
        {
            // The circuit now finishes a data center before crossing to the next.
            // A saved route holds the old interleave and is only rebuilt when its
            // set of worlds changes, so drop it - and the cursor into it - and let
            // the next trip lay out a fresh circuit.
            PriorityScoutRoute?.Clear();
            PriorityNextWorld = string.Empty;
            PriorityNextItem = 0;
            Version = 41;
        }
        if (Version < 42)
        {
            // High-volume stock was held to the same 20% as a slow flip, so a trip
            // could walk past popcorn that was a thousand gil cheaper than home and
            // come back having bought nothing. Idle gil earns less than a thin
            // margin that turns over daily.
            ProcurementFastMoverRoiPercent = 10m;
            Version = 42;
        }
        if (Version < 43)
        {
            foreach (var discovered in ProcurementRules.Where(x => x.DiscoveredAutomatically))
                discovered.PreferredStock = false;
            Version = 43;
        }
        Version = Math.Max(Version, 43);
        ProcurementPreferredCoverageDays = Math.Clamp(ProcurementPreferredCoverageDays, 0.25m, 7m);
        ProcurementSecondaryCoverageDays = Math.Clamp(ProcurementSecondaryCoverageDays, 0.25m, 7m);
        ProcurementOpportunisticCoverageDays = Math.Clamp(ProcurementOpportunisticCoverageDays, 0.1m, 2m);
        ProcurementCoverageOvershootDays = Math.Clamp(ProcurementCoverageOvershootDays, 0m, 1m);
        ProcurementHighVolumeMinimumSalesPerDay = Math.Clamp(ProcurementHighVolumeMinimumSalesPerDay, 10m, 10_000m);
        ProcurementHighVolumeMinimumValuePerSlot = Math.Clamp(ProcurementHighVolumeMinimumValuePerSlot, 150_000u, 100_000_000u);
        ProcurementAbsoluteMinimumRoiPercent = Math.Clamp(ProcurementAbsoluteMinimumRoiPercent, 8m, 1_000m);
        ProcurementHighVolumeRoiPercent = Math.Clamp(ProcurementHighVolumeRoiPercent, 8m, 1_000m);
        ProcurementLowValueRoiPercent = Math.Clamp(ProcurementLowValueRoiPercent, 8m, 1_000m);
        ProcurementAnchorAbsorptionDays = Math.Clamp(ProcurementAnchorAbsorptionDays, 0m, 0.5m);
        ProcurementEmergencyMaximumSlotsPerItem = Math.Clamp(ProcurementEmergencyMaximumSlotsPerItem, 1, 60);
        // A nonsense reading must never widen a margin. Anything outside the range
        // the game can actually charge falls back to the standard rate.
        if (ObservedMarketTaxPercent is < 0m or >= 100m)
            ObservedMarketTaxPercent = 5m;
        PreferredPortfolioTargetPercent = Math.Clamp(PreferredPortfolioTargetPercent, 0m, 100m);
        OpportunisticPortfolioMaximumPercent = Math.Clamp(OpportunisticPortfolioMaximumPercent, 0m, 100m);
        ProcurementMinimumProfitPerSaleSlot = Math.Min(ProcurementMinimumProfitPerSaleSlot, 100_000_000u);
        MarketDiscoveryCacheHours = Math.Clamp(MarketDiscoveryCacheHours, 1, 168);
        MarketDiscoveryRegion = string.IsNullOrWhiteSpace(MarketDiscoveryRegion) ? "NA" : MarketDiscoveryRegion.Trim();
        ScoutKnowledgeMaxAgeHours = Math.Clamp(ScoutKnowledgeMaxAgeHours, 1, 168);
        ProcurementFillRoiPercent = Math.Clamp(ProcurementFillRoiPercent, 0m, 1_000m);
        ProcurementFastMoverRoiPercent = Math.Clamp(ProcurementFastMoverRoiPercent, 0m, 1_000m);
        PriorityScoutRoute ??= [];
        PriorityItemsPerWorld = Math.Clamp(PriorityItemsPerWorld, 1, 40);
        ProcurementBufferValueTarget = Math.Min(ProcurementBufferValueTarget, 999_999_999u);
        PriorityWorldsPerTrip = Math.Clamp(PriorityWorldsPerTrip, 1, 40);
        PriorityMinutesPerTrip = Math.Clamp(PriorityMinutesPerTrip, 5, 480);
        HomePriceMaxAgeMinutes = Math.Clamp(HomePriceMaxAgeMinutes, 5, 30);
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
        ShoppingTripMinimumFreeSaleSlots = Math.Clamp(ShoppingTripMinimumFreeSaleSlots, 0, 60);
        ShoppingTripMinimumGil = Math.Min(ShoppingTripMinimumGil, 100_000_000u);
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
