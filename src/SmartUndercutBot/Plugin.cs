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
    [PluginService] private static IPluginLog PluginLog { get; set; } = null!;

    private readonly WindowSystem windowSystem = new("SmartUndercutBot");
    private readonly ConfigurationService configuration;
    private readonly MarketDataService marketData;
    private readonly AutomationController automation;
    private readonly UniversalisService universalis;
    private readonly ProcurementController procurement;
    private readonly BagListingController bagListing;
    private readonly DashboardWindow dashboard;
    private readonly GuidedProcurementWindow guidedProcurement;
    private readonly TaskbarAttentionService taskbarAttention;

    public Plugin()
    {
        configuration = new ConfigurationService(PluginInterface);
        var automationLog = new AutomationLog(PluginLog);
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
        taskbarAttention = new TaskbarAttentionService();
        procurement = new ProcurementController(
            Framework,
            PlayerState,
            CommandManager,
            retainerListings,
            universalis,
            new ProcurementPlannerService(),
            new MarketPurchaseService(ObjectTable, GameGui, DataManager, automationLog),
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
        dashboard = new DashboardWindow(
            configuration, automation, procurement, bagListing, universalis, procurementLedger, marketData,
            automationLog);
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
            HelpMessage = "Open Smart Undercutter. Use /sub guided for a manual deal route or /sub stop to abort.",
        });
        PluginLog.Information("Smart Undercutter initialized.");
    }

    private void OnCommand(string _, string arguments)
    {
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            automation.Halt("Stopped with /sub stop.");
            procurement.Halt("Procurement stopped with /sub stop.");
            bagListing.Halt("Bag listing stopped with /sub stop.");
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
        procurement.Dispose();
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
