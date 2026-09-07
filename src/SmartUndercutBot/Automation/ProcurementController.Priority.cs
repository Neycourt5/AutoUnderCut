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

    private async Task<IReadOnlyList<ProcurementMarketItem>> ScanPriorityRegionAsync(
        IReadOnlyList<ProcurementRule> rules, string home, CancellationToken token)
    {
        var regional = universalis.ScanAsync(rules, "North-America", token);
        var local = universalis.ScanAsync(rules, home, token);
        IReadOnlyList<ProcurementMarketItem> region;
        try { region = await regional.ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Cached scouting is a hint, never a prerequisite for live prices.
            region = [];
            log.Add(AutomationLogLevel.Warning, "Regional price hints unavailable; using rotating live scouts across all four data centers.");
        }
        var homeMarkets = await local.ConfigureAwait(false);
        return homeMarkets.Select(m => m with
        {
            Listings = m.Listings.Concat(region.Where(r => r.ItemId == m.ItemId).SelectMany(r => r.Listings)
                .Where(l => !l.WorldName.Equals(home, StringComparison.OrdinalIgnoreCase))).Distinct().ToArray(),
        }).ToArray();
    }

    private void PrepareScoutRoute()
    {
        var config = configuration.Current;
        var hints = priorityDemand.SelectMany(m => m.Listings.Select(l =>
        {
            var reference = HomePriceReference.Summarize(m.ItemId, m.ItemName, l.IsHighQuality,
                m.Listings.Where(h => h.WorldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase)).ToArray()).Reference;
            return (Listing: l, Score: Math.Max(0m, reference * 0.95m - l.PricePerUnit * 1.05m) * l.Quantity);
        })).Where(x => stockHuntRules.Any(r => r.ItemId == x.Listing.ItemId &&
            ResaleStockPolicy.BuyableQuality(r, x.Listing.IsHighQuality, config.BuyHighQualityOnly)) &&
            !retainerListings.OwnedRetainerIds.Contains(x.Listing.RetainerId)).ToArray();
        var scores = hints.GroupBy(x => x.Listing.WorldName, StringComparer.OrdinalIgnoreCase)
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
        for (var i = 0; i < config.PriorityScoutRoute.Count; i++)
        {
            var world = config.PriorityScoutRoute[i];
            // Reserve half the quick scan for rotating high-volume flips, so
            // stale or missing regional hints cannot hide new bargains forever.
            var hinted = hints.Where(x => x.Listing.WorldName.Equals(world, StringComparison.OrdinalIgnoreCase) && x.Score > 0)
                .OrderByDescending(x => x.Score).Select(x => x.Listing.ItemId).Distinct().Take(Math.Max(1, limit / 2));
            var offset = i * Math.Max(1, limit / 2) % stockHuntRules.Count;
            var rotated = stockHuntRules.Skip(offset).Concat(stockHuntRules.Take(offset)).Select(r => r.ItemId);
            var resumed = world.Equals(config.PriorityNextWorld, StringComparison.OrdinalIgnoreCase) && config.PriorityNextItem != 0
                ? new[] { config.PriorityNextItem } : [];
            scoutItems[world] = resumed.Concat(hinted).Concat(rotated).Distinct().Take(limit).ToHashSet();
        }
        log.Add(AutomationLogLevel.Information,
            $"REGIONAL SCOUT: compared {hints.Length} cached offers; first stops " +
            $"{string.Join(" > ", stockHuntWorlds.Skip(1).Take(config.PriorityWorldsPerTrip))}. " +
            "Cached offers only choose where to look; purchasing requires live observations.");
        configuration.Save();
    }

    private bool ShouldScoutItem(uint item) => scoutItems.GetValueOrDefault(WorldName)?.Contains(item) == true;

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
            HighQualityOnly: config.BuyHighQualityOnly, ResaleListings: homePrices.Values.SelectMany(x => x).ToArray()));
        if (compared.Orders.Count == 0) { FinishShopping(reason + " No remaining compared deals fit the current limits; returning to retainers."); return; }
        // A more expensive replacement may still pass ROI, but was not the deal
        // that won this comparison. Allow the observed price or better only.
        Plan = compared with { Orders = compared.Orders.Select(o => o with
            { MaximumAcceptableUnitPrice = Math.Min(o.PricePerUnit, o.MaximumAcceptableUnitPrice) }).ToArray() };
        stockHuntScanning = false;
        currentStockHuntRule = null;
        comparisonBuyingStarted = timeProvider.GetUtcNow();
        log.Add(AutomationLogLevel.Information,
            $"SCOUT COMPARISON: {priorityWorldsCompleted} away world(s), {scoutListings.Count} observed listing(s); " +
            $"selected {Plan.Orders.Count} buy(s), expected profit {Plan.ExpectedProfit:N0}. Revisiting selected deals for fresh price and tax checks.");
        BeginExecution(ProcurementRunMode.AutomaticPurchase, preserveTrip: true);
    }

    private void BeginPriorityShopping(IReadOnlyList<ProcurementMarketItem> markets)
    {
        priorityDemand = markets;
        var config = configuration.Current;
        stockHuntRules = config.ProcurementRules.Where(x => x.Enabled && x.ItemId != 0 && !x.LiquidateOnly)
            .DistinctBy(x => x.ItemId)
            .Where(rule => new[] { false, true }.Any(quality =>
                ResaleStockPolicy.BuyableQuality(rule, quality, config.BuyHighQualityOnly) &&
                markets.Where(m => m.ItemId == rule.ItemId).SelectMany(m => m.RecentSales)
                    .Where(s => s.IsHighQuality == quality && s.PricePerUnit > 0 &&
                        s.SoldAt >= timeProvider.GetUtcNow().AddDays(-7) && s.SoldAt <= timeProvider.GetUtcNow())
                    .Sum(s => (long)s.Quantity) >= Math.Max(1, rule.MinimumWeeklyUnitsSold)))
            .OrderBy(x => x.TourPriority)
            .ThenByDescending(rule => markets.Where(m => m.ItemId == rule.ItemId)
                .SelectMany(m => m.RecentSales).Where(s => s.SoldAt >= timeProvider.GetUtcNow().AddDays(-7))
                .Sum(s => (long)s.Quantity))
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
        PrepareScoutRoute();
        scoutListings.Clear();
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
            $"Scout up to {config.PriorityItemsPerWorld} items per away world, two worlds per data center. " +
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
                    HighQualityOnly: config.BuyHighQualityOnly, ResaleListings: resale));
                candidates.AddRange(plan.Orders);
            }
        }
        var order = candidates.OrderByDescending(x => x.ExpectedProfit).ThenBy(x => x.PricePerUnit).FirstOrDefault();
        var exceptional = order is not null && ShoppingScoutPolicy.IsExceptional(order, config.ProcurementMinimumRoiPercent);
        if (stockHuntWorldIndex > 0)
        {
            scoutListings.RemoveAll(x => x.WorldName == WorldName && x.ItemId == rule.ItemId);
            scoutListings.AddRange(rows);
        }
        foreach (var quality in new[] { false, true })
        {
            if (!ResaleStockPolicy.BuyableQuality(rule, quality, config.BuyHighQualityOnly)) continue;
            var qualified = rows.Where(x => x.IsHighQuality == quality && !retainerListings.OwnedRetainerIds.Contains(x.RetainerId)).ToArray();
            var decision = order is not null && order.IsHighQuality == quality
                ? $"{(exceptional ? "Buy exceptional deal now" : "Save for comparison after scouting")}: x{order.Quantity} at {order.PricePerUnit:N0}; resale {order.TargetSalePrice:N0}, ceiling {order.MaximumAcceptableUnitPrice:N0}, expected profit {order.ExpectedProfit:N0}."
                : homePrices.GetValueOrDefault(rule.ItemId)?.Length is not > 0 ? "No confirmed home resale listings; skip buying."
                : "No deal passes home sales, ROI, demand, budget and stock limits.";
            observations.Add(new(timeProvider.GetUtcNow(), WorldName, rule.ItemName, quality,
                qualified.Length, qualified.Sum(x => (long)x.Quantity), qualified.Select(x => x.PricePerUnit).DefaultIfEmpty().Min(), decision));
            if (observations.Count > 300) observations.RemoveAt(0);
            var observed = observations[^1];
            log.Add(AutomationLogLevel.Information,
                $"PRICE CHECK {observed.At:O} {WorldName}: {rule.ItemName} {(quality ? "HQ" : "NQ")}: " +
                $"{observed.Listings} listings / {observed.Units} units, lowest {observed.Lowest:N0}. {decision}");
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
