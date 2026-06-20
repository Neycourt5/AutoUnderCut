using Dalamud.Configuration;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool AutomationEnabled { get; set; }
    public bool AllowAutomaticWrites { get; set; }
    public bool OpenDashboardOnRetainer { get; set; } = true;
    public int MinimumDelayMs { get; set; } = 250;
    public int MaximumDelayMs { get; set; } = 450;
    public int MarketRequestTimeoutSeconds { get; set; } = 10;
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
        MinimumDelayMs = Math.Clamp(MinimumDelayMs, 100, 60_000);
        MaximumDelayMs = Math.Clamp(MaximumDelayMs, MinimumDelayMs, 60_000);
        MarketRequestTimeoutSeconds = Math.Clamp(MarketRequestTimeoutSeconds, 2, 60);
        MaximumUpdatesPerSession = Math.Clamp(MaximumUpdatesPerSession, 1, 200);
        GlobalRule ??= new PricingRule();
        PerItemRules ??= [];
    }
}
