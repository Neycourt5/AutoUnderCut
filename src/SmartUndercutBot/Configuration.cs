using Dalamud.Configuration;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;
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
    public int SameItemCacheSeconds { get; set; } = 30;
    public int MaximumUpdatesPerSession { get; set; } = 200;
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
            if (SameItemCacheSeconds == 0)
                SameItemCacheSeconds = 30;
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
        MinimumDelayMs = Math.Clamp(MinimumDelayMs, 100, 60_000);
        MaximumDelayMs = Math.Clamp(MaximumDelayMs, MinimumDelayMs, 60_000);
        MarketRequestTimeoutSeconds = Math.Clamp(MarketRequestTimeoutSeconds, 2, 60);
        MarketRequestCooldownMs = Math.Clamp(MarketRequestCooldownMs, 1000, 10_000);
        SameItemCacheSeconds = Math.Clamp(SameItemCacheSeconds, 1, 300);
        RepeatMinimumMinutes = Math.Clamp(RepeatMinimumMinutes, 5, 1_440);
        RepeatMaximumMinutes = Math.Clamp(RepeatMaximumMinutes, RepeatMinimumMinutes, 1_440);
        MaximumUpdatesPerSession = Math.Clamp(MaximumUpdatesPerSession, 1, 200);
        GlobalRule ??= new PricingRule();
        PerItemRules ??= [];
    }
}
