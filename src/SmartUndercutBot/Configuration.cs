using Dalamud.Configuration;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 9;
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
    public int MarketRequestCooldownMs { get; set; } = 1200;
    public int MaximumUpdatesPerSession { get; set; } = 200;
    public bool AutomaticProcurementEnabled { get; set; }
    public bool AllowAutomaticPurchases { get; set; }
    public bool AllowAutomaticListing { get; set; }
    public int ProcurementIntervalMinutes { get; set; } = 30;
    public uint ProcurementBudget { get; set; } = 5_000_000;
    public int ProcurementTargetSaleSlots { get; set; } = 60;
    public int ProcurementInventoryReserve { get; set; } = 10;
    public decimal ProcurementMinimumRoiPercent { get; set; } = 20m;
    public uint ProcurementMinimumProfitPerUnit { get; set; } = 100;
    public string ProcurementDataCenter { get; set; } = string.Empty;
    public string MarketBoardTravelCommand { get; set; } = "/li mb";
    public List<ProcurementRule> ProcurementRules { get; set; } = [];
    public PricingRule GlobalRule { get; set; } = new();
    public Dictionary<uint, PricingRule> PerItemRules { get; set; } = [];

    public PricingRule GetEffectiveRule(uint itemId) =>
        PerItemRules.TryGetValue(itemId, out var rule) ? rule.Clone() : GlobalRule.Clone();

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
        MinimumDelayMs = Math.Clamp(MinimumDelayMs, 100, 60_000);
        MaximumDelayMs = Math.Clamp(MaximumDelayMs, MinimumDelayMs, 60_000);
        MarketRequestTimeoutSeconds = Math.Clamp(MarketRequestTimeoutSeconds, 2, 60);
        MarketRequestCooldownMs = Math.Clamp(MarketRequestCooldownMs, 1000, 10_000);
        RepeatMinimumMinutes = Math.Clamp(RepeatMinimumMinutes, 5, 1_440);
        RepeatMaximumMinutes = Math.Clamp(RepeatMaximumMinutes, RepeatMinimumMinutes, 1_440);
        MaximumUpdatesPerSession = Math.Clamp(MaximumUpdatesPerSession, 1, 200);
        ProcurementIntervalMinutes = Math.Clamp(ProcurementIntervalMinutes, 15, 1_440);
        ProcurementBudget = Math.Clamp(ProcurementBudget, 1_000u, 100_000_000u);
        ProcurementTargetSaleSlots = Math.Clamp(ProcurementTargetSaleSlots, 1, 200);
        ProcurementInventoryReserve = Math.Clamp(ProcurementInventoryReserve, 1, 100);
        ProcurementMinimumRoiPercent = Math.Clamp(ProcurementMinimumRoiPercent, 0m, 1_000m);
        ProcurementMinimumProfitPerUnit = Math.Clamp(ProcurementMinimumProfitPerUnit, 0u, 100_000_000u);
        ProcurementDataCenter ??= string.Empty;
        MarketBoardTravelCommand = string.IsNullOrWhiteSpace(MarketBoardTravelCommand)
            ? "/li mb"
            : MarketBoardTravelCommand.Trim();
        ProcurementRules ??= [];
        foreach (var rule in ProcurementRules)
        {
            rule.ItemName ??= string.Empty;
            rule.TargetStackSize = Math.Clamp(rule.TargetStackSize, 1, 999);
            rule.MaximumSaleSlots = Math.Clamp(rule.MaximumSaleSlots, 1, 60);
            rule.MinimumWeeklyUnitsSold = Math.Clamp(rule.MinimumWeeklyUnitsSold, 0, 1_000_000);
        }
        GlobalRule ??= new PricingRule();
        PerItemRules ??= [];
    }
}
