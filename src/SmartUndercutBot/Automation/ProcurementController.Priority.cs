using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

public sealed record ShoppingObservation(DateTimeOffset At, string World, string Item, bool HighQuality,
    int Listings, long Units, uint Lowest, string Decision);

public sealed partial class ProcurementController
{
    private bool priorityShopping;
    private IReadOnlyList<ProcurementMarketItem> priorityDemand = [];
    private readonly Dictionary<uint, ProcurementMarketListing[]> homePrices = [];
    private readonly Dictionary<uint, DateTimeOffset> homePriceTimes = [];
    private readonly List<ShoppingObservation> observations = [];
    private DateTimeOffset priorityDepartedAt;
    private int priorityWorldsCompleted;
    private readonly List<ProcurementMarketListing> scoutListings = [];
    // Cached aggregate observations that only ever choose where to look.
    private IReadOnlyList<MarketPriceHint> regionHints = [];
    // Remember routing hints between trips. Prices can change within this window;
    // every selected purchase still needs a fresh live price and tax check.
    private readonly Dictionary<(string World, uint Item), DateTimeOffset> scoutObservedAt = new();
    private readonly Dictionary<string, HashSet<uint>> scoutItems = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? comparisonBuyingStarted;
    public IReadOnlyList<ShoppingObservation> RecentPrices => observations;

    /// <summary>The home-world prices every away deal is measured against.</summary>
    public IReadOnlyList<HomePriceSummary> HomeReferencePrices => homePrices
        .SelectMany(entry => new[] { false, true }
            .Select(quality => HomePriceReference.Summarize(entry.Key,
                stockHuntRules.FirstOrDefault(r => r.ItemId == entry.Key)?.ItemName ??
                configuration.Current.ProcurementRules.FirstOrDefault(r => r.ItemId == entry.Key)?.ItemName ??
                $"#{entry.Key}",
                quality, entry.Value)))
        .Where(x => x.Listings > 0)
        .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DateTimeOffset? HomePricesUpdatedAt =>
        homePriceTimes.Count == 0 ? null : homePriceTimes.Values.Max();

    public decimal HomeSalesPerDay(uint itemId, bool highQuality) => SalesVelocityPolicy.DailyUnits(
        priorityDemand.FirstOrDefault(m => m.ItemId == itemId), highQuality, timeProvider.GetUtcNow());

    private decimal RuleSalesPerDay(ProcurementRule rule) => new[] { false, true }
        .Where(q => ResaleStockPolicy.BuyableQuality(rule, q, configuration.Current.BuyHighQualityOnly))
        .Select(q => HomeSalesPerDay(rule.ItemId, q)).DefaultIfEmpty().Max();

    /// <summary>
    /// Two very different requests. The home world needs real competing listings
    /// and sale history, so it uses the full endpoint. The rest of the region only
    /// has to answer "where is this cheap and how fast does it move", which the
    /// cached aggregate endpoint does in one request per hundred items instead of
    /// a hundred listings and a hundred sales for every item on every data center.
    /// </summary>
    private async Task<IReadOnlyList<ProcurementMarketItem>> ScanPriorityRegionAsync(
        IReadOnlyList<ProcurementRule> rules, string home, CancellationToken token)
    {
        var regional = universalis.FetchPriceHintsAsync(rules, "North-America", token);
        var local = universalis.ScanAsync(rules, home, token);
        try { regionHints = await regional.ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Cached scouting is a hint, never a prerequisite for live prices.
            regionHints = [];
            log.Add(AutomationLogLevel.Warning,
                "Regional price hints unavailable; using rotating live scouts across all four data centers.");
        }
        // The hints deliberately do not feed home demand. Their velocity is the
        // whole region's rate, and shopping priority is judged on what actually
        // sells on the home world.
        return await local.ConfigureAwait(false);
    }

    private void PrepareScoutRoute()
    {
        var config = configuration.Current;
        // Score each cached hint by the margin a target stack would carry against
        // the home reference. Hints have no retainer id, but they only ever choose
        // where to travel: the home world is excluded here, and every purchase is
        // revalidated live with our own retainers excluded.
        var hints = regionHints
            .Where(h => !h.WorldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase) &&
                        ProcurementTravelPolicy.CanShopOnWorld(h.WorldName))
            .Select(h => (Hint: h, Rule: stockHuntRules.FirstOrDefault(r => r.ItemId == h.ItemId &&
                ResaleStockPolicy.BuyableQuality(r, h.IsHighQuality, config.BuyHighQualityOnly))))
            .Where(x => x.Rule is not null)
            .Select(x =>
            {
                var reference = HomePriceReference.Summarize(x.Hint.ItemId, x.Rule!.ItemName, x.Hint.IsHighQuality,
                    priorityDemand.FirstOrDefault(m => m.ItemId == x.Hint.ItemId)?.Listings
                        .Where(h => h.WorldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase)).ToArray() ?? []).Reference;
                var units = (uint)Math.Max(1, x.Rule.TargetStackSize);
                return (x.Hint, Score: Math.Max(0m, reference * 0.95m - x.Hint.PricePerUnit * 1.05m) * units);
            })
            .ToArray();
        var scores = hints.GroupBy(x => x.Hint.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Score), StringComparer.OrdinalIgnoreCase);
        var expectedWorlds = NorthAmericaWorlds.Where(w => !w.Equals(homeWorld, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Keep the route stable across retainer checkpoints; repricing the hints
        // on each trip must not reorder worlds behind the saved cursor forever.
        if (config.PriorityScoutRoute.Count != expectedWorlds.Length ||
            !config.PriorityScoutRoute.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedWorlds))
            config.PriorityScoutRoute = ShoppingScoutPolicy.BuildRoute(NorthAmericaWorlds, homeWorld, scores).ToList();
        var resume = config.PriorityScoutRoute.FindIndex(w => w.Equals(config.PriorityNextWorld, StringComparison.OrdinalIgnoreCase));
        stockHuntWorlds = new[] { homeWorld }.Concat(config.PriorityScoutRoute.Skip(Math.Max(0, resume))).ToList();
        scoutItems.Clear();
        var limit = Math.Max(1, config.PriorityItemsPerWorld);
        // The food and potion block is checked on every world; only the secondary
        // lines rotate. Rotating the whole list is what let the window walk past
        // the gemdraughts entirely by the third stop of a circuit.
        var preferredItems = stockHuntRules.Where(r => r.PreferredStock).Select(r => r.ItemId).ToArray();
        var secondaryItems = stockHuntRules.Where(r => !r.PreferredStock).Select(r => r.ItemId).ToArray();
        for (var i = 0; i < config.PriorityScoutRoute.Count; i++)
        {
            var world = config.PriorityScoutRoute[i];
            // Cached bargains get a quarter of the stop rather than half: the rest
            // belongs to the food block and to the busiest lines by average volume,
            // which is what a sale slot can actually be turned over on.
            var hinted = hints.Where(x => x.Hint.WorldName.Equals(world, StringComparison.OrdinalIgnoreCase) && x.Score > 0)
                .OrderByDescending(x => HomeSalesPerDay(x.Hint.ItemId, x.Hint.IsHighQuality))
                .ThenByDescending(x => x.Score).Select(x => x.Hint.ItemId).Distinct().Take(Math.Max(1, limit / 4));
            var resumed = world.Equals(config.PriorityNextWorld, StringComparison.OrdinalIgnoreCase) && config.PriorityNextItem != 0
                ? new[] { config.PriorityNextItem } : [];
            scoutItems[world] = ShoppingScoutPolicy
                .SelectWorldItems(preferredItems, secondaryItems, resumed, hinted, i, limit).ToHashSet();
        }
        // A fresh cached world needs no physical visit. Without this, "skip known
        // prices" travelled to empty scans and then treated them as server failures.
        stockHuntWorlds = new[] { homeWorld }.Concat(stockHuntWorlds.Skip(1)
            .Where(w => scoutItems[w].Any(item => !ScoutKnowledgeIsFresh(w, item)))).ToList();
        log.Add(AutomationLogLevel.Information,
            $"REGIONAL SCOUT: compared {hints.Length} cached offers; {preferredItems.Length} food and potion line(s) " +
            $"are priced on every world. First stops " +
            $"{string.Join(" > ", stockHuntWorlds.Skip(1).Take(config.PriorityWorldsPerTrip))}. " +
            "Cached offers only choose where to look; purchasing requires live observations.");
        configuration.Save();
    }

    private bool ShouldScoutItem(uint item) =>
        scoutItems.GetValueOrDefault(WorldName)?.Contains(item) == true && !ScoutKnowledgeIsFresh(WorldName, item);

    private TimeSpan ScoutKnowledgeLife =>
        TimeSpan.FromHours(Math.Max(1, configuration.Current.ScoutKnowledgeMaxAgeHours));

    /// <summary>
    /// Food and potions are re-read on every visit. They are what the portfolio is
    /// built on and their prices move, so a day-old reading is not a reason to skip
    /// them - and because every world's scan carries the block, no world is dropped
    /// from the circuit for being "already known".
    /// </summary>
    private bool ScoutKnowledgeIsFresh(string world, uint item) =>
        !IsPreferredStock(item) &&
        scoutObservedAt.TryGetValue((world, item), out var at) &&
        timeProvider.GetUtcNow() - at <= ScoutKnowledgeLife;

    private void PruneStaleScoutKnowledge()
    {
        var now = timeProvider.GetUtcNow();
        var stale = scoutObservedAt.Where(x => now - x.Value > ScoutKnowledgeLife).Select(x => x.Key).ToArray();
        foreach (var key in stale)
        {
            scoutObservedAt.Remove(key);
            scoutListings.RemoveAll(l => l.ItemId == key.Item &&
                string.Equals(l.WorldName, key.World, StringComparison.OrdinalIgnoreCase));
        }
        if (stale.Length > 0)
            log.Add(AutomationLogLevel.Information,
                $"Dropped {stale.Length} price observation(s) older than {ScoutKnowledgeLife.TotalHours:N0}h; " +
                $"{scoutObservedAt.Count} still current and will not be re-read.");
    }

    /// <summary>How much of the planned scouting is already known and need not be re-read.</summary>
    public (int Known, int Total) ScoutKnowledgeCoverage
    {
        get
        {
            int known = 0, total = 0;
            foreach (var (world, items) in scoutItems)
                foreach (var item in items)
                {
                    total++;
                    if (ScoutKnowledgeIsFresh(world, item)) known++;
                }
            return (known, total);
        }
    }

    private void FinishPriorityScouting(string reason)
    {
        var config = configuration.Current;
        var markets = priorityDemand.Select(m => m with
        {
            Listings = scoutListings.Where(l => l.ItemId == m.ItemId && HomePriceIsFresh(l.ItemId)).ToArray(),
        }).ToArray();
        var compared = planner.BuildPlan(new(markets, ShoppingRules(stockHuntRules), SpendableGil(), AvailablePurchaseSlots(),
            Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve),
            config.ProcurementMinimumRoiPercent, config.ProcurementMinimumProfitPerUnit,
            HomeWorld: homeWorld, OwnedRetainerIds: retainerListings.OwnedRetainerIds,
            OwnedStock: CollectOwnedStock(), MaximumWeeklySalesSharePercent: config.ProcurementWeeklySalesSharePercent,
            HighQualityOnly: config.BuyHighQualityOnly, ResaleListings: homePrices.Values.SelectMany(x => x).ToArray(),
            Portfolio: config.PortfolioGates, PortfolioCapacitySlots: PortfolioCapacitySlots()));
        LogPortfolioDecisions("SCOUT", compared);
        compared = TopUpEmptySaleSlots(compared, markets);
        if (compared.Orders.Count == 0)
        {
            FinishShopping(reason + " No compared deal met the portfolio quality bar; leaving the slots empty " +
                           $"rather than buying low-value stock. {compared.Summary}");
            return;
        }
        // A more expensive replacement may still pass ROI, but was not the deal
        // that won this comparison. Allow the observed price or better only.
        Plan = compared with { Orders = compared.Orders.Select(o => o with
            { MaximumAcceptableUnitPrice = Math.Min(o.PricePerUnit, o.MaximumAcceptableUnitPrice) }).ToArray() };
        stockHuntScanning = false;
        currentStockHuntRule = null;
        comparisonBuyingStarted = timeProvider.GetUtcNow();
        log.Add(AutomationLogLevel.Information,
            $"SCOUT COMPARISON: {priorityWorldsCompleted} away world(s), {scoutListings.Count} observed listing(s); " +
            $"selected {Plan.Orders.Count} buy(s), expected profit {Plan.ExpectedProfit:N0}. {Plan.Summary}. " +
            "Revisiting selected deals for fresh price and tax checks.");
        BeginExecution(ProcurementRunMode.AutomaticPurchase, preserveTrip: true);
    }

    /// <summary>
    /// A thinner margin is worth taking on stock that belongs in the portfolio, but
    /// never as an excuse to occupy a slot. The fill pass runs with the
    /// opportunistic cap set to zero, so it can only reach preferred core stock and
    /// genuinely high-liquidity secondary stock; the slot-value gate and every other
    /// guard still apply, and the fill bar is still a real profit after fees.
    /// </summary>
    private ProcurementPlan TopUpEmptySaleSlots(ProcurementPlan compared, IReadOnlyList<ProcurementMarketItem> markets)
    {
        var config = configuration.Current;
        var uncovered = Math.Max(0, Math.Min(repricing.LastKnownFreeSaleSlots ?? 0, config.ProcurementTargetSaleSlots) - ResaleBagSlots);
        var free = Math.Min(AvailablePurchaseSlots(), uncovered) - compared.Orders.Count;
        if (free <= 0 || config.ProcurementFillRoiPercent >= config.ProcurementMinimumRoiPercent)
            return compared;

        var budget = (uint)Math.Max(0, (long)SpendableGil() - compared.TotalCost);
        if (budget == 0) return compared;

        var taken = compared.Orders.Select(x => (x.WorldName, x.ListingId)).ToHashSet();
        var remaining = markets.Select(m => m with
        {
            Listings = m.Listings.Where(l => !taken.Contains((l.WorldName, l.ListingId))).ToArray(),
        }).ToArray();
        var owned = CollectOwnedStock()
            .Concat(compared.Orders.Select(o => new StockExposure(o.ItemId, o.IsHighQuality, o.Quantity, 1, o.Tier)))
            .ToArray();
        var fill = planner.BuildPlan(new(remaining, ShoppingRules(stockHuntRules), budget, free,
            Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve - compared.SaleSlots),
            config.ProcurementFillRoiPercent, config.ProcurementMinimumProfitPerUnit,
            HomeWorld: homeWorld, OwnedRetainerIds: retainerListings.OwnedRetainerIds,
            OwnedStock: owned, MaximumWeeklySalesSharePercent: config.ProcurementWeeklySalesSharePercent,
            HighQualityOnly: config.BuyHighQualityOnly, ResaleListings: homePrices.Values.SelectMany(x => x).ToArray(),
            Portfolio: config.PortfolioGates with { OpportunisticMaximumPercent = 0m },
            PortfolioCapacitySlots: PortfolioCapacitySlots()));
        // Belt and braces: the cap already excludes them, and PollListings refuses
        // one again before buying, but never carry an opportunistic fill order.
        var accepted = fill.Orders.Where(o => o.Tier != PortfolioTier.Opportunistic).ToArray();
        if (accepted.Length == 0)
        {
            log.Add(AutomationLogLevel.Information,
                $"STOCK TOP-UP: {free} sale slot(s) stay empty. No preferred or high-liquidity deal qualified at the " +
                $"{config.ProcurementFillRoiPercent:N0}% fill margin, and low-value stock is not worth a retainer slot.");
            return compared;
        }

        log.Add(AutomationLogLevel.Information,
            $"STOCK TOP-UP: {free} sale slot(s) would have been left empty; added {accepted.Length} preferred or " +
            $"high-liquidity deal(s) at the {config.ProcurementFillRoiPercent:N0}% fill margin. " +
            "Opportunistic stock cannot use this lower bar.");
        // Mirror the planner's landed cost, including the buyer fee, because
        // `accepted` may be a subset of the fill plan.
        var addedCost = accepted.Aggregate(0UL, (sum, o) => sum +
            (ulong)decimal.Ceiling((decimal)o.PricePerUnit * o.Quantity * 1.05m));
        return compared with
        {
            Orders = compared.Orders.Concat(accepted.Select(o => o with { IsFillOrder = true })).ToArray(),
            TotalCost = (uint)Math.Min(uint.MaxValue, (ulong)compared.TotalCost + addedCost),
            ExpectedProfit = (uint)Math.Min(uint.MaxValue,
                (ulong)compared.ExpectedProfit + accepted.Aggregate(0UL, (sum, o) => sum + o.ExpectedProfit)),
            SaleSlots = compared.SaleSlots + accepted.Sum(o => o.SaleSlots),
        };
    }

    private void BeginPriorityShopping(IReadOnlyList<ProcurementMarketItem> markets)
    {
        priorityDemand = markets;
        var config = configuration.Current;
        stockHuntRules = config.ProcurementRules.Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .DistinctBy(x => x.ItemId)
            // Preferred food and potions are never dropped for a thin sales week.
            // Everything else has to show the demand to earn a price check.
            .Where(rule => rule.PreferredStock || new[] { false, true }.Any(quality =>
                ResaleStockPolicy.BuyableQuality(rule, quality, config.BuyHighQualityOnly) &&
                markets.Where(m => m.ItemId == rule.ItemId).SelectMany(m => m.RecentSales)
                    .Where(s => s.IsHighQuality == quality && s.PricePerUnit > 0 &&
                        s.SoldAt >= timeProvider.GetUtcNow().AddDays(-7) && s.SoldAt <= timeProvider.GetUtcNow())
                    .Sum(s => (long)s.Quantity) >= Math.Max(1, rule.MinimumWeeklyUnitsSold)))
            // The scan walks this order, so the block comes first on every world:
            // a trip cut short by the clock or a bad board still priced the food.
            .OrderBy(x => x.PreferredStock ? 0 : 1)
            .ThenByDescending(RuleSalesPerDay)
            .ThenBy(x => x.TourPriority)
            .ThenBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        if (stockHuntRules.Count == 0 || ShoppingWaitReason is not null)
        {
            detail = stockHuntRules.Count == 0
                ? "No recent home-world sales for the configured flips. Retainer checks and price searches will retry."
                : $"Price search finished; buying is {ShoppingWaitReason}. Retainer checks continue.";
            priorityShopping = false;
            log.Add(AutomationLogLevel.Information, detail);
            return;
        }
        if (!TryGetHomeWorld(out homeWorld) || !lifestream.IsAvailable || lifestream.IsBusy)
        {
            HaltForRetry("Wait for the home world and Lifestream before starting priority shopping.");
            return;
        }

        SeedHomePricesFromRepricing();
        PruneStaleScoutKnowledge();
        PrepareScoutRoute();
        comparisonBuyingStarted = null;
        purchasedSlotsByItem.Clear();
        stockHuntWorldIndex = stockHuntRuleIndex = 0;
        successfulLiveScans = failedLiveScans = consecutiveFailedWorlds = worldSuccessfulScans = 0;
        priorityWorldsCompleted = 0;
        priorityDepartedAt = timeProvider.GetUtcNow();
        currentStockHuntRule = null;
        currentOrder = null;
        routeOutcome = string.Empty;
        activeRunMode = ProcurementRunMode.AutomaticPurchase;
        runAfterScan = ProcurementRunMode.None;
        stockHuntScanning = true;
        ledger.ClearBagStockQueue();
        repricing.Halt("Paused for home price checks and priority shopping.");
        ownsRetainerPause = true;
        market.CloseRetainerList();
        log.Add(AutomationLogLevel.Information,
            $"PRIORITY SHOPPING: check {stockHuntRules.Count} flips on {homeWorld}, then Aether -> Primal -> Crystal -> Dynamis. " +
            $"Check the {stockHuntRules.Count(r => r.PreferredStock)} food and potion line(s) on every world plus " +
            $"rotating flips, up to {config.PriorityItemsPerWorld} items per away world, two worlds per data center. " +
            $"Compare after {config.PriorityWorldsPerTrip} worlds or {config.PriorityMinutesPerTrip} minutes; buy early only at 100%+ net ROI.");
        TravelToCurrentWorld();
    }

    private bool PriorityTripShouldReturn()
    {
        if (!priorityShopping) return false;
        if (stockHuntWorldIndex == 0)
        {
            // The home scan happens at home, so it is not time away from the
            // retainers and a flat 20 minutes is the wrong budget for it: with 51
            // flips to check it expired mid-scan, threw the prices away and started
            // over, which is why the route never left the home world. Budget it
            // against the size of the list instead.
            var homeBudget = TimeSpan.FromSeconds(Math.Max(1_200, stockHuntRules.Count * 30));
            if (timeProvider.GetUtcNow() - priorityDepartedAt < homeBudget) return false;
            if (homePrices.Count > 0)
            {
                // Prices already gathered are worth travelling on. Restarting the
                // scan from scratch would only expire again at the same point.
                log.Add(AutomationLogLevel.Warning,
                    $"Home price checks ran long; travelling with the {homePrices.Count} price(s) already gathered.");
                stockHuntRuleIndex = stockHuntRules.Count;
                FinishStockHuntWorld();
                return true;
            }
            FinishShopping("Home price checks ran long without gathering any prices. Returning to retainers.");
            return true;
        }
        var config = configuration.Current;
        // Keep shopping while the buffer is short on either spread or value. A bag
        // full of cheap dye meets the stack target without being worth selling, and
        // returning then leaves gil idle and the good stock unbought.
        var roomToBuy = AvailablePurchaseSlots() > 0 ||
            !ResaleStockPolicy.BufferIsComfortable(ResaleBagSlots, ComfortableStockTarget,
                ResaleBagValue, config.ProcurementBufferValueTarget);
        if (roomToBuy && SpendableGil() > 0 &&
            market.FreeInventorySlots > config.ProcurementInventoryReserve &&
            priorityWorldsCompleted < config.PriorityWorldsPerTrip &&
            timeProvider.GetUtcNow() - priorityDepartedAt < TimeSpan.FromMinutes(config.PriorityMinutesPerTrip))
            return false;
        SavePriorityCursor();
        FinishPriorityScouting("Scout checkpoint reached. The next trip resumes here.");
        return true;
    }

    /// <summary>
    /// Estimated resale value of the trading buffer, priced from the home reference.
    /// Items with no known home price contribute nothing rather than a guess.
    /// </summary>
    public ulong ResaleBagValue
    {
        get
        {
            ulong total = 0;
            foreach (var stock in CollectBagStock())
            {
                if (!homePrices.TryGetValue(stock.ItemId, out var listings)) continue;
                var reference = HomePriceReference.Summarize(
                    stock.ItemId, string.Empty, stock.IsHighQuality, listings).Reference;
                total += (ulong)reference * stock.Quantity;
            }
            return total;
        }
    }

    // A retainer pass reads the live home board for every listing it reprices, so
    // those prices are already paid for. Reuse anything inside the freshness window
    // instead of sweeping the same items again at the start of every trip.
    private void SeedHomePricesFromRepricing()
    {
        var maxAge = HomeReferenceMaxAge;
        var now = timeProvider.GetUtcNow();
        foreach (var stale in homePriceTimes.Where(x => now - x.Value > maxAge).Select(x => x.Key).ToArray())
        {
            homePrices.Remove(stale);
            homePriceTimes.Remove(stale);
        }
        var reused = 0;
        foreach (var (itemId, observed) in repricing.ObservedHomePrices)
        {
            if (now - observed.At > maxAge) continue;
            if (homePriceTimes.TryGetValue(itemId, out var have) && have >= observed.At) continue;
            homePrices[itemId] = observed.Listings
                .Where(x => x.PricePerUnit > 0 && x.Quantity > 0)
                .Select(x => new ProcurementMarketListing(itemId, 0, x.RetainerId, homeWorld, 0,
                    x.PricePerUnit, x.Quantity, x.IsHighQuality))
                .ToArray();
            homePriceTimes[itemId] = observed.At;
            reused++;
        }
        if (reused > 0)
            log.Add(AutomationLogLevel.Information,
                $"Reused {reused} home price(s) already read during the retainer pass; those items are not re-checked.");
    }

    // Use the same lifetime when skipping a home read and when permitting a buy.
    // Previously a quote was reused for 24 hours, then rejected after 30 minutes.
    private TimeSpan HomeReferenceMaxAge => TimeSpan.FromMinutes(Math.Clamp(configuration.Current.HomePriceMaxAgeMinutes, 5, 30));
    private bool HomePriceIsFresh(uint itemId) =>
        homePriceTimes.TryGetValue(itemId, out var at) &&
        timeProvider.GetUtcNow() - at <= HomeReferenceMaxAge;

    private void SavePriorityCursor()
    {
        if (!priorityShopping || stockHuntWorldIndex == 0) return;
        configuration.Current.PriorityNextWorld = WorldName;
        configuration.Current.PriorityNextItem = stockHuntRuleIndex < stockHuntRules.Count
            ? stockHuntRules[stockHuntRuleIndex].ItemId : 0;
        configuration.Save();
    }

    private void ObservePriorityItem(IReadOnlyList<LivePurchaseListing> live)
    {
        var rule = currentStockHuntRule!;
        var rows = live.Select(x => new ProcurementMarketListing(x.ItemId, x.ListingId, x.RetainerId,
            WorldName, 0, x.PricePerUnit, x.Quantity, x.IsHighQuality)).ToArray();
        if (stockHuntWorldIndex == 0)
        {
            homePrices[rule.ItemId] = rows;
            homePriceTimes[rule.ItemId] = timeProvider.GetUtcNow();
        }
        else if (!HomePriceIsFresh(rule.ItemId))
        {
            SavePriorityCursor();
            FinishPriorityScouting("Home resale prices need refreshing.");
            return;
        }
        var sales = priorityDemand.FirstOrDefault(x => x.ItemId == rule.ItemId)?.RecentSales ?? [];
        var config = configuration.Current;
        var candidates = new List<ProcurementOrder>();
        var rules = ShoppingRules([rule]);
        var owned = CollectOwnedStock();
        var budget = SpendableGil();
        var slots = AvailablePurchaseSlots();
        if (homePrices.TryGetValue(rule.ItemId, out var home) && home.Length > 0)
        {
            // On the home world, a bargain must beat BOTH recorded sales and the
            // next competing listing. Never use the bargain itself as the resale
            // anchor, nor manufacture a price from our own retainers.
            foreach (var candidate in rows)
            {
                var resale = stockHuntWorldIndex == 0
                    ? home.Where(x => x.ListingId != candidate.ListingId).ToArray() : home;
                var plan = planner.BuildPlan(new(
                    [new(rule.ItemId, rule.ItemName, [candidate], sales)], rules,
                    budget, slots,
                    Math.Max(0, (int)market.FreeInventorySlots - config.ProcurementInventoryReserve),
                    config.ProcurementMinimumRoiPercent, config.ProcurementMinimumProfitPerUnit,
                    HomeWorld: homeWorld, OwnedRetainerIds: retainerListings.OwnedRetainerIds,
                    OwnedStock: owned, MaximumWeeklySalesSharePercent: config.ProcurementWeeklySalesSharePercent,
                    HighQualityOnly: config.BuyHighQualityOnly, ResaleListings: resale,
                    Portfolio: config.PortfolioGates, PortfolioCapacitySlots: PortfolioCapacitySlots()));
                candidates.AddRange(plan.Orders);
            }
        }
        var order = candidates.OrderBy(x => PortfolioPolicy.Rank(x.Tier))
            .ThenByDescending(x => x.ExpectedProfit).ThenBy(x => x.PricePerUnit).FirstOrDefault();
        var exceptional = order is not null && ShoppingScoutPolicy.IsExceptional(order, config.ProcurementMinimumRoiPercent);
        if (stockHuntWorldIndex > 0)
        {
            scoutListings.RemoveAll(x => x.WorldName == WorldName && x.ItemId == rule.ItemId);
            scoutListings.AddRange(rows);
            scoutObservedAt[(WorldName, rule.ItemId)] = timeProvider.GetUtcNow();
        }
        foreach (var quality in new[] { false, true })
        {
            if (!ResaleStockPolicy.BuyableQuality(rule, quality, config.BuyHighQualityOnly)) continue;
            var qualified = rows.Where(x => x.IsHighQuality == quality && !retainerListings.OwnedRetainerIds.Contains(x.RetainerId)).ToArray();
            var decision = order is not null && order.IsHighQuality == quality
                ? $"{order.Tier.ToString().ToUpperInvariant()}. {(exceptional ? "Buy exceptional deal now" : "Save for comparison after scouting")}: x{order.Quantity} at {order.PricePerUnit:N0}; resale {order.TargetSalePrice:N0}, ceiling {order.MaximumAcceptableUnitPrice:N0}, expected profit {order.ExpectedProfit:N0}, estimated turnover {order.EstimatedDaysToSell:N2} days."
                : homePrices.GetValueOrDefault(rule.ItemId)?.Length is not > 0 ? "No confirmed home resale listings; skip buying."
                : "No deal passes home sales, ROI, portfolio quality, demand, budget and stock limits.";
            observations.Add(new(timeProvider.GetUtcNow(), WorldName, rule.ItemName, quality,
                qualified.Length, qualified.Sum(x => (long)x.Quantity), qualified.Select(x => x.PricePerUnit).DefaultIfEmpty().Min(), decision));
            if (observations.Count > 300) observations.RemoveAt(0);
            var observed = observations[^1];
            log.Add(AutomationLogLevel.Information,
                $"PRICE CHECK {observed.At:O} {WorldName}: {rule.ItemName} {(quality ? "HQ" : "NQ")}: " +
                $"{observed.Listings} listings / {observed.Units} units, lowest {observed.Lowest:N0}. " +
                $"Home sales {HomeSalesPerDay(rule.ItemId, quality):N1} units/day. {decision}");
        }
        if (!exceptional || order is null) { AdvanceStockHuntRule(); return; }
        Plan = new(timeProvider.GetUtcNow(), Plan.Orders.Append(order).ToArray(),
            (uint)Math.Min(uint.MaxValue, (ulong)Plan.TotalCost + (ulong)order.PricePerUnit * order.Quantity),
            (uint)Math.Min(uint.MaxValue, (ulong)Plan.ExpectedProfit + order.ExpectedProfit), Plan.SaleSlots + 1);
        currentOrder = order;
        // Listings are already loaded. A separate tick revalidates the exact live
        // candidate and tax through the normal purchase guards before submitting.
        nextActionAt = timeProvider.GetUtcNow();
        Wait(ProcurementState.WaitingForListings, $"Checking the live {order.ItemName} deal before buying.", 30);
    }
}
