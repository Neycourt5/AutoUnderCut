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
    private readonly DashboardWindow dashboard;

    public Plugin()
    {
        configuration = new ConfigurationService(PluginInterface);
        var automationLog = new AutomationLog(PluginLog);
        var procurementLedger = new ProcurementLedger();
        marketData = new MarketDataService(MarketBoard, configuration);
        var retainerListings = new RetainerListingService(ClientState, ObjectTable, GameGui, DataManager);
        automation = new AutomationController(
            Framework,
            retainerListings,
            marketData,
            new PricingStrategyService(),
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
        procurement = new ProcurementController(
            Framework,
            PlayerState,
            CommandManager,
            retainerListings,
            universalis,
            new ProcurementPlannerService(),
            new MarketPurchaseService(ObjectTable, GameGui),
            new VnavmeshService(PluginInterface),
            procurementLedger,
            automation,
            configuration,
            automationLog);
        dashboard = new DashboardWindow(
            configuration, automation, procurement, universalis, procurementLedger, marketData, automationLog);
        automation.RetainerInterfaceOpened += OnRetainerInterfaceOpened;

        windowSystem.AddWindow(dashboard);
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleDashboard;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleDashboard;
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Smart Undercutter dashboard.",
        });
        PluginLog.Information("Smart Undercutter initialized.");
    }

    private void OnCommand(string _, string arguments)
    {
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            automation.Halt("Stopped with /sub stop.");
            procurement.Halt("Procurement stopped with /sub stop.");
        }
        else
            ToggleDashboard();
    }

    private void OnRetainerInterfaceOpened()
    {
        if (configuration.Current.OpenDashboardOnRetainer)
            dashboard.IsOpen = true;
    }

    private void ToggleDashboard() => dashboard.Toggle();

    public void Dispose()
    {
        automation.RetainerInterfaceOpened -= OnRetainerInterfaceOpened;
        procurement.Dispose();
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
