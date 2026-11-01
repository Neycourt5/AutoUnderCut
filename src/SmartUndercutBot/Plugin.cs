using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using SmartUndercutBot.Windows;

namespace SmartUndercutBot;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/sub";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IMarketBoard MarketBoard { get; set; } = null!;
    [PluginService] private static IGameInteropProvider Interop { get; set; } = null!;
    [PluginService] private static IPluginLog PluginLog { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;

    private readonly WindowSystem windowSystem = new("SmartUndercutBot");
    private readonly ConfigurationService configuration;
    private readonly MarketDataService marketData;
    private readonly MarketPurchaseService marketPurchase;
    private readonly AutomationController automation;
    private readonly UniversalisService universalis;
    private readonly ProcurementController procurement;
    private readonly BagListingController bagListing;
    private readonly StockAutomationController stockAutomation;
    private readonly WealthHistoryService wealthHistory;
    private readonly DashboardWindow dashboard;
    private readonly GuidedProcurementWindow guidedProcurement;
    private readonly TaskbarAttentionService taskbarAttention;
    private readonly SaddlebagStatisticsProvider marketStatistics;
    private readonly MarketDiscoveryService marketDiscovery;
    private readonly PriceMemoryService priceMemory;
    private readonly MarketScanService marketScan;
    private readonly RetainerSaleService retainerSales;

    public Plugin()
    {
        configuration = new ConfigurationService(PluginInterface);
        var automationLog = new AutomationLog(PluginLog,
            Path.Combine(PluginInterface.ConfigDirectory.FullName, "logs"));
        automationLog.Add(AutomationLogLevel.Information,
            $"Session log: {automationLog.SessionFilePath ?? "disabled"}");
        if (configuration.UnreadableSettingsCopy is { } unreadable)
            automationLog.Add(AutomationLogLevel.Error,
                $"The settings file could not be loaded, so defaults are in use. The original was kept as {unreadable}.");
        priceMemory = new PriceMemoryService(PluginInterface.ConfigDirectory.FullName,
            () => PlayerState.IsLoaded ? PlayerState.ContentId : 0, automationLog);
        var procurementLedger = new ProcurementLedger();
        marketData = new MarketDataService(MarketBoard, configuration);
        var retainerListings = new RetainerListingService(ClientState, ObjectTable, GameGui, DataManager);
        var pricingStrategy = new PricingStrategyService();
        automation = new AutomationController(
            Framework,
            retainerListings,
            marketData,
            pricingStrategy,
            new PortfolioValuationService(),
            configuration,
            procurementLedger,
            automationLog,
            portfolioCache: new PortfolioCacheService(PluginInterface.ConfigDirectory.FullName,
                () => PlayerState.IsLoaded ? PlayerState.ContentId : 0, automationLog));
        universalis = new UniversalisService(PlayerState, DataManager, automationLog);
        marketStatistics = new SaddlebagStatisticsProvider(
            () => configuration.Current.MarketDiscoveryEnabled,
            () => configuration.Current.MarketDiscoveryRegion,
            automationLog);
        marketDiscovery = new MarketDiscoveryService(
            marketStatistics, universalis.LookupItem, configuration, automationLog);
        if (configuration.Current.ProcurementRules.Count == 0)
        {
            configuration.Current.ProcurementRules.AddRange(universalis.CreateFavoriteRules());
            configuration.Save();
        }
        if (!configuration.Current.FavoriteRulesBackfilled)
        {
            foreach (var rule in universalis.CreateFavoriteRules())
            {
                if (configuration.Current.ProcurementRules.Any(x => x.ItemId == rule.ItemId))
                    continue;
                configuration.Current.ProcurementRules.Add(rule);
                automationLog.Add(AutomationLogLevel.Information,
                    $"Added the missing favorite {rule.ItemName} as preferred stock, priced on every world.");
            }
            configuration.Current.FavoriteRulesBackfilled = true;
            configuration.Save();
        }
        if (!configuration.Current.DyeRulesSeeded || !configuration.Current.MateriaRulesSeeded)
        {
            var liquidate = universalis.CreateLiquidationRules();
            foreach (var rule in liquidate)
            {
                var existing = configuration.Current.ProcurementRules.FirstOrDefault(x => x.ItemId == rule.ItemId);
                if (existing is null)
                    configuration.Current.ProcurementRules.Add(rule);
                else if (existing.LiquidateOnly)
                {
                    // Refresh a rule this plugin seeded, never one the user made
                    // buyable on purpose.
                    existing.ListFromBags = true;
                    existing.BagReserveQuantity = 0;
                    existing.MaximumSaleSlots = rule.MaximumSaleSlots;
                    existing.TargetStackSize = rule.TargetStackSize;
                }
            }
            configuration.Current.DyeRulesSeeded = true;
            configuration.Current.MateriaRulesSeeded = true;
            configuration.Save();
        }
        if (!configuration.Current.TomeMaterialRulesSeeded)
        {
            var tome = universalis.CreateTomeMaterialRules();
            foreach (var rule in tome)
            {
                var existing = configuration.Current.ProcurementRules.FirstOrDefault(x => x.ItemId == rule.ItemId);
                if (existing is null)
                    configuration.Current.ProcurementRules.Add(rule);
                else if (existing.LiquidateOnly)
                {
                    existing.ListFromBags = true;
                    existing.BagReserveQuantity = 0;
                    existing.TargetStackSize = rule.TargetStackSize;
                    existing.MaximumSaleSlots = rule.MaximumSaleSlots;
                }
            }
            automationLog.Add(AutomationLogLevel.Information,
                $"SELL-OFF matched {tome.Count}/{ResaleStockPolicy.TomeMaterialNames.Length} tomestone material name(s) " +
                "in the item sheet; any missing name is listed in the Shopping item table.");
            configuration.Current.TomeMaterialRulesSeeded = true;
            configuration.Save();
        }
        if (!configuration.Current.BuyableDyeRulesSeeded)
        {
            foreach (var rule in universalis.CreateBuyableDyeRules()
                         .Concat(universalis.CreateTradeableMateriaRules()))
                if (configuration.Current.ProcurementRules.All(x => x.ItemId != rule.ItemId))
                    configuration.Current.ProcurementRules.Add(rule);
            configuration.Current.BuyableDyeRulesSeeded = true;
            configuration.Save();
        }
        taskbarAttention = new TaskbarAttentionService();
        marketPurchase = new MarketPurchaseService(ObjectTable, GameGui, DataManager, automationLog, MarketBoard, Interop);
        procurement = new ProcurementController(
            Framework,
            PlayerState,
            CommandManager,
            retainerListings,
            universalis,
            new ProcurementPlannerService(),
            marketPurchase,
            new VnavmeshService(PluginInterface),
            new LifestreamService(PluginInterface),
            taskbarAttention,
            procurementLedger,
            automation,
            configuration,
            automationLog,
            discovery: marketDiscovery,
            priceMemory: () => priceMemory.Memory);
        bagListing = new BagListingController(
            Framework,
            CommandManager,
            PlayerState,
            retainerListings,
            universalis,
            pricingStrategy,
            configuration,
            procurementLedger,
            automation,
            procurement,
            automationLog);
        // Defaults carry no cost floors: running on them after a settings file failed to
        // load would undercut purchased stock below cost. Nothing starts until the
        // user has looked at the kept copy.
        automation.IsStartBlocked = () => procurement.IsActive || bagListing.IsBusy ||
                                          configuration.UnreadableSettingsCopy is not null;
        procurement.IsStartBlocked = () => automation.IsActive || bagListing.IsBusy || bagListing.IsAutomaticRunDue ||
                                           configuration.UnreadableSettingsCopy is not null;
        stockAutomation = new StockAutomationController(configuration, automation, procurement, bagListing);
        automation.ReadBagPortfolio = () => EstimateBagPortfolio(retainerListings);
        automation.CanRefreshPortfolioInventory = () => PlayerState.IsLoaded && !bagListing.IsBusy;
        wealthHistory = new WealthHistoryService(Framework, automation.PortfolioSnapshot, automationLog,
            PluginInterface.ConfigDirectory.FullName,
            characterId: () => PlayerState.IsLoaded ? PlayerState.ContentId : 0,
            canRecord: () => retainerListings.IsPlayerInventoryReady && !automation.IsActive && !bagListing.IsBusy);
        marketScan = new MarketScanService(
            new MarketScanSource(universalis, marketStatistics),
            configuration,
            () => priceMemory.Memory,
            HomeWorldName,
            () => automation.KnownRetainerNames,
            () => retainerListings.OwnedRetainerIds,
            universalis.LookupItem,
            universalis.ResolveWorldName,
            automationLog);
        retainerSales = new RetainerSaleService(
            ChatGui,
            configuration,
            () => priceMemory.Memory,
            () => automation.KnownListings.Select(x => (x.ItemId, x.IsHighQuality, x.Quantity, x.AskingUnitPrice)),
            universalis.LookupItem,
            automationLog);
        retainerSales.SaleRecorded += OnSaleRecorded;
        Framework.Update += OnFrameworkUpdate;
        dashboard = new DashboardWindow(
            configuration, automation, procurement, bagListing, universalis, procurementLedger, marketData,
            automationLog, stockAutomation, wealthHistory, priceMemory, marketScan);
        guidedProcurement = new GuidedProcurementWindow(procurement);
        automation.RetainerInterfaceOpened += OnRetainerInterfaceOpened;
        procurement.GuidedReviewRequested += OnGuidedReviewRequested;

        windowSystem.AddWindow(dashboard);
        windowSystem.AddWindow(guidedProcurement);
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleDashboard;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleDashboard;
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Smart Undercutter. /sub start keeps retainers stocked; /sub stop stops all automation; /sub guided opens a manual deal route.",
        });
        PluginLog.Information("Smart Undercutter initialized.");
    }

    private IReadOnlyList<PortfolioBagStockEstimate> EstimateBagPortfolio(IRetainerListingService retainers)
    {
        var holdings = retainers.ReadBagListingCandidates()
            .Select(x => new PortfolioBagHolding(x.ItemId, x.ItemName, x.IsHighQuality, x.Quantity)).ToArray();
        var prices = holdings.GroupBy(x => (x.ItemId, x.IsHighQuality)).Select(group =>
        {
            uint? marketPrice = null;
            if (automation.ObservedHomePrices.TryGetValue(group.Key.ItemId, out var observed))
                marketPrice = observed.Listings.Where(x => x.IsHighQuality == group.Key.IsHighQuality &&
                        !retainers.OwnedRetainerIds.Contains(x.RetainerId) &&
                        MarketPriceSafety.IsSafeAutomaticUnitPrice(group.First().ItemName, x.PricePerUnit, x.Quantity))
                    .Select(x => (uint?)x.PricePerUnit).Min();
            marketPrice ??= automation.LastEstimatedHomePrice(group.Key.ItemId, group.Key.IsHighQuality);
            var cost = configuration.Current.GetEffectiveRule(group.Key.ItemId).CostBasis;
            return new PortfolioBagPrice(group.Key.ItemId, group.Key.IsHighQuality, marketPrice, cost > 0 ? cost : null);
        }).ToArray();
        return BagStockValuationService.Estimate(holdings, configuration.Current.ProcurementRules,
            (uint)Math.Max(0, configuration.Current.BagListingReservePerItem), prices,
            configuration.Current.Fees.MarketTaxPercent);
    }

    private void OnCommand(string _, string arguments)
    {
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            stockAutomation.Stop("Stopped with /sub stop. Start again from the dashboard when ready.");
        }
        else if (arguments.Trim().Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            stockAutomation.Start();
            dashboard.IsOpen = true;
        }
        else if (arguments.Trim().Equals("guided", StringComparison.OrdinalIgnoreCase))
            procurement.RunGuidedNow();
        else
            ToggleDashboard();
    }

    private static string? HomeWorldName()
    {
        if (!PlayerState.IsLoaded)
            return null;
        try
        {
            var name = PlayerState.HomeWorld.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            priceMemory.Update();
            // The measured capture share counts only time the sale tracker heard.
            if (configuration.Current.SaleTrackingEnabled && PlayerState.IsLoaded)
                priceMemory.Memory.MarkWatching(DateTimeOffset.UtcNow);
            marketScan.Update();
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Price memory or market scan update failed.");
        }
    }

    // A sold slot is refilled at the next retainer check. Bring that check forward,
    // so the slot sits empty for about a minute rather than until the next pass.
    private void OnSaleRecorded(RetainerSale sale)
    {
        if (configuration.Current.RelistAfterSale)
            automation.RequestEarlyCheck(
                $"Sold {sale.ItemName}{(sale.IsHighQuality ? " HQ" : string.Empty)} x{sale.Quantity}; refilling the slot.");
    }

    private void OnRetainerInterfaceOpened()
    {
        if (configuration.Current.OpenDashboardOnRetainer)
            dashboard.IsOpen = true;
    }

    private void OnGuidedReviewRequested()
    {
        guidedProcurement.IsOpen = true;
        guidedProcurement.BringToFront();
    }

    private void ToggleDashboard() => dashboard.Toggle();

    public void Dispose()
    {
        automation.RetainerInterfaceOpened -= OnRetainerInterfaceOpened;
        procurement.GuidedReviewRequested -= OnGuidedReviewRequested;
        Framework.Update -= OnFrameworkUpdate;
        retainerSales.SaleRecorded -= OnSaleRecorded;
        retainerSales.Dispose();
        marketScan.Dispose();
        priceMemory.Dispose();
        wealthHistory.Dispose();
        procurement.Dispose();
        marketPurchase.Dispose();
        taskbarAttention.Dispose();
        bagListing.Dispose();
        automation.Dispose();
        universalis.Dispose();
        marketStatistics.Dispose();
        marketData.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleDashboard;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleDashboard;
        windowSystem.RemoveAllWindows();
    }
}
