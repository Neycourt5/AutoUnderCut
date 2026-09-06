using SmartUndercutBot.Services;

namespace SmartUndercutBot.Automation;

/// <summary>One entry point for starting and stopping the complete retainer workflow.</summary>
public sealed class StockAutomationController(
    ConfigurationService configuration,
    AutomationController automation,
    ProcurementController procurement,
    BagListingController bagListing)
{
    public bool IsBusy => automation.IsActive || procurement.IsActive || bagListing.IsBusy;
    public bool NeedsAttention =>
        (configuration.Current.AutomationEnabled || configuration.Current.AllowAutomaticWrites ||
         configuration.Current.AllowAutomaticPurchases || configuration.Current.AllowAutomaticListing) &&
        !procurement.IsActive &&
        ((!procurement.IsWaitingToReturnHome && automation.RequiresManualRestart) || procurement.RequiresManualRestart);

    public string? StartIssue => IsBusy ? "Wait for the current operation to finish, or stop it first."
        : !bagListing.IsRetainerListOpen ? "Open the summoning-bell retainer list to start."
        : procurement.TravelReadinessIssue
          ?? (!configuration.Current.ProcurementRules.Any(x => x.Enabled && x.ItemId != 0)
              ? "Choose at least one item to buy in Shopping."
              : null);

    public bool Start()
    {
        if (StartIssue is not null)
            return false;
        configuration.Current.EnableStockAutomation();
        configuration.Save();
        procurement.ResumeAutomatic();
        bagListing.ResumeAutomatic();
        // Re-read every retainer before using previous capacity to plan a trip.
        automation.StartNow();
        return true;
    }

    public void Stop(string reason = "Stopped by user. Press Start keeping retainers stocked to resume.")
    {
        // Disarm scheduling as well as active work, including after a plugin reload.
        configuration.Current.DisableStockAutomation();
        automation.Halt(reason);
        procurement.Halt(reason);
        bagListing.Halt(reason);
        configuration.Save();
    }
}
