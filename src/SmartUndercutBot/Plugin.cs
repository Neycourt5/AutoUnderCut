using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Automation;
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

    public Plugin()
    {
        configuration = new ConfigurationService(PluginInterface);
        var automationLog = new AutomationLog(PluginLog,
            Path.Combine(PluginInterface.ConfigDirectory.FullName, "logs"));
        automationLog.Add(AutomationLogLevel.Information,
            $"Session log: {automationLog.SessionFilePath ?? "disabled"}");
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
            automationLog);
        universalis = new UniversalisService(PlayerState, DataManager);
        if (configuration.Current.ProcurementRules.Count == 0)
        {
            configuration.Current.ProcurementRules.AddRange(universalis.CreateFavoriteRules());
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
            automationLog);
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
        automation.IsStartBlocked = () => procurement.IsActive || bagListing.IsBusy;
        procurement.IsStartBlocked = () => automation.IsActive || bagListing.IsBusy || bagListing.IsAutomaticRunDue;
        stockAutomation = new StockAutomationController(configuration, automation, procurement, bagListing);
        wealthHistory = new WealthHistoryService(Framework, automation.PortfolioSnapshot, automationLog,
            PluginInterface.ConfigDirectory.FullName);
        dashboard = new DashboardWindow(
            configuration, automation, procurement, bagListing, universalis, procurementLedger, marketData,
            automationLog, stockAutomation, wealthHistory);
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
        wealthHistory.Dispose();
        procurement.Dispose();
        marketPurchase.Dispose();
        taskbarAttention.Dispose();
        bagListing.Dispose();
        automation.Dispose();
        universalis.Dispose();
        marketData.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleDashboard;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleDashboard;
        windowSystem.RemoveAllWindows();
    }
}
