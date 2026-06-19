using Dalamud.Configuration;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool AutomationEnabled { get; set; }
    public bool AllowAutomaticWrites { get; set; }
    public bool OpenDashboardOnRetainer { get; set; } = true;
    public int MinimumDelayMs { get; set; } = 800;
    public int MaximumDelayMs { get; set; } = 1500;
    public int MarketRequestTimeoutSeconds { get; set; } = 10;
    public int MaximumMarketDataAgeSeconds { get; set; } = 180;
    public int MaximumUpdatesPerSession { get; set; } = 20;
    public string MarketApiBaseUrl { get; set; } = "https://universalis.app/api/v2";
    public PricingRule GlobalRule { get; set; } = new();
    public Dictionary<uint, PricingRule> PerItemRules { get; set; } = [];

    public PricingRule GetEffectiveRule(uint itemId) =>
        PerItemRules.TryGetValue(itemId, out var rule) ? rule.Clone() : GlobalRule.Clone();

    public void Normalize()
    {
        MinimumDelayMs = Math.Clamp(MinimumDelayMs, 250, 60_000);
        MaximumDelayMs = Math.Clamp(MaximumDelayMs, MinimumDelayMs, 60_000);
        MarketRequestTimeoutSeconds = Math.Clamp(MarketRequestTimeoutSeconds, 2, 60);
        MaximumMarketDataAgeSeconds = Math.Clamp(MaximumMarketDataAgeSeconds, 15, 3600);
        MaximumUpdatesPerSession = Math.Clamp(MaximumUpdatesPerSession, 1, 20);
        GlobalRule ??= new PricingRule();
        PerItemRules ??= [];
    }
}
