using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Windows;

public sealed class DashboardWindow : Window
{
    private readonly ConfigurationService configuration;
    private readonly AutomationController automation;
    private readonly IMarketDataService marketData;
    private readonly AutomationLog log;
    private int newItemId;
    private uint? selectedItemId;
    private bool configurationDirty;

    public DashboardWindow(
        ConfigurationService configuration,
        AutomationController automation,
        IMarketDataService marketData,
        AutomationLog log)
        : base("Smart Undercutter##Dashboard")
    {
        this.configuration = configuration;
        this.automation = automation;
        this.marketData = marketData;
        this.log = log;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 440),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("DashboardTabs"))
            return;

        if (ImGui.BeginTabItem("Status"))
        {
            DrawStatus();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Queue"))
        {
            DrawQueue();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Pricing Rules"))
        {
            DrawRules();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Safety & Data"))
        {
            DrawSafetySettings();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Audit Log"))
        {
            DrawLog();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawStatus()
    {
        var status = automation.Status;
        var stateColor = status.State switch
        {
            AutomationState.Faulted or AutomationState.Halted => new Vector4(1f, 0.35f, 0.3f, 1f),
            AutomationState.Completed => new Vector4(0.35f, 0.9f, 0.45f, 1f),
            AutomationState.Idle => new Vector4(0.7f, 0.7f, 0.7f, 1f),
            _ => new Vector4(0.35f, 0.75f, 1f, 1f),
        };
        ImGui.TextColored(stateColor, status.State.ToString());
        ImGui.SameLine();
        ImGui.TextWrapped(status.Detail);
        ImGui.Separator();

        var config = configuration.Current;
        var enabled = config.AutomationEnabled;
        if (ImGui.Checkbox("Start automatically at the summoning bell", ref enabled))
        {
            config.AutomationEnabled = enabled;
            SaveConfiguration();
        }

        var allRetainers = config.ProcessAllRetainers;
        if (ImGui.Checkbox("Automatically process every retainer", ref allRetainers))
        {
            config.ProcessAllRetainers = allRetainers;
            SaveConfiguration();
        }

        var repeatRuns = config.RepeatBellRuns;
        if (ImGui.Checkbox("Repeat bell runs while idle", ref repeatRuns))
        {
            config.RepeatBellRuns = repeatRuns;
            SaveConfiguration();
        }
        if (repeatRuns)
        {
            var minimumMinutes = config.RepeatMinimumMinutes;
            if (InputInt("Minimum minutes between runs", ref minimumMinutes, 5, 1_440))
            {
                config.RepeatMinimumMinutes = minimumMinutes;
                SaveConfiguration();
            }
            var maximumMinutes = config.RepeatMaximumMinutes;
            if (InputInt("Maximum minutes between runs", ref maximumMinutes, config.RepeatMinimumMinutes, 1_440))
            {
                config.RepeatMaximumMinutes = maximumMinutes;
                SaveConfiguration();
            }
            ImGui.TextWrapped("The next interval is randomized within this range. Scheduled runs continue only while logged in, stationary, and the summoning-bell retainer list remains open.");
        }

        var writes = config.AllowAutomaticWrites;
        if (ImGui.Checkbox("Arm autonomous price writes", ref writes))
        {
            config.AllowAutomaticWrites = writes;
            SaveConfiguration();
        }
        ImGui.TextColored(
            writes ? new Vector4(1f, 0.72f, 0.2f, 1f) : new Vector4(0.55f, 0.85f, 0.65f, 1f),
            writes
                ? "ARMED: accepted decisions are submitted without per-item confirmation."
                : "DRY RUN: decisions are logged, but the client is not modified.");

        ImGui.Spacing();
        if (ImGui.Button("Start / Rescan"))
            automation.StartNow();
        ImGui.SameLine();
        if (ImGui.Button("Emergency Stop"))
            automation.Halt();

        ImGui.Spacing();
        ImGui.Text($"Progress: {Math.Min(status.CurrentIndex + 1, status.TotalListings)} / {status.TotalListings}");
        if (status.TotalRetainers > 0)
            ImGui.Text($"Retainer: {status.CurrentRetainer} / {status.TotalRetainers}");
        ImGui.Text($"Updates submitted: {status.UpdatesSubmitted}");
        if (status.NextActionAt.HasValue)
        {
            var wait = Math.Max(0, (status.NextActionAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
            ImGui.Text($"Next action in: {wait:F1}s");
        }
    }

    private void DrawQueue()
    {
        var queue = automation.QueueSnapshot();
        if (queue.Count == 0)
        {
            ImGui.TextDisabled("No listings are queued.");
            return;
        }

        if (!ImGui.BeginTable("RetainerQueue", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, -1)))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Live lowest", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();
        foreach (var entry in queue)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted((entry.Index + 1).ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Listing.ItemName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Listing.IsHighQuality ? "HQ" : "NQ");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{entry.Listing.CurrentPrice:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Decision?.LowestMarketPrice is { } lowest ? $"{lowest:N0}" : "-");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Decision?.TargetPrice is { } target ? $"{target:N0}" : "—");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Status);
        }
        ImGui.EndTable();
    }

    private void DrawRules()
    {
        if (ImGui.CollapsingHeader("Global rule", ImGuiTreeNodeFlags.DefaultOpen))
            configurationDirty |= DrawPricingRule("global", configuration.Current.GlobalRule);

        ImGui.Separator();
        ImGui.TextUnformatted("Per-item overrides");
        ImGui.Separator();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Item ID", ref newItemId);
        ImGui.SameLine();
        if (ImGui.Button("Add override") && newItemId > 0)
        {
            var itemId = (uint)newItemId;
            configuration.Current.PerItemRules[itemId] = configuration.Current.GlobalRule.Clone();
            selectedItemId = itemId;
            configurationDirty = true;
        }

        if (ImGui.BeginTable("ItemOverrides", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg,
                new Vector2(260 * ImGuiHelpers.GlobalScale, 150 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("Item ID");
            ImGui.TableSetupColumn("Mode");
            ImGui.TableHeadersRow();
            foreach (var pair in configuration.Current.PerItemRules.OrderBy(x => x.Key))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(pair.Key.ToString(), selectedItemId == pair.Key,
                        ImGuiSelectableFlags.SpanAllColumns))
                    selectedItemId = pair.Key;
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(pair.Value.Mode.ToString());
            }
            ImGui.EndTable();
        }

        if (selectedItemId is { } selected && configuration.Current.PerItemRules.TryGetValue(selected, out var rule))
        {
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text($"Override for item #{selected}");
            configurationDirty |= DrawPricingRule($"item{selected}", rule);
            if (ImGui.Button($"Remove override##{selected}"))
            {
                configuration.Current.PerItemRules.Remove(selected);
                selectedItemId = null;
                configurationDirty = true;
            }
            ImGui.EndGroup();
        }

        DrawSaveButton();
    }

    private bool DrawPricingRule(string id, PricingRule rule)
    {
        var changed = false;
        ImGui.PushID(id);
        var pricingMode = rule.Mode;
        if (DrawEnumCombo("Pricing mode", ref pricingMode))
        {
            rule.Mode = pricingMode;
            changed = true;
        }
        var undercutAmount = rule.UndercutAmount;
        if (InputUInt("Undercut amount", ref undercutAmount, 0, 1_000_000))
        {
            rule.UndercutAmount = undercutAmount;
            changed = true;
        }
        var minimumPrice = rule.MinimumPrice;
        if (InputUInt("Minimum price", ref minimumPrice, 1, 999_999_999))
        {
            rule.MinimumPrice = minimumPrice;
            changed = true;
        }
        var costBasis = rule.CostBasis;
        if (InputUInt("Cost basis", ref costBasis, 0, 999_999_999))
        {
            rule.CostBasis = costBasis;
            changed = true;
        }

        var margin = (float)rule.MinimumMarginPercent;
        if (ImGui.DragFloat("Minimum margin %", ref margin, 0.1f, 0, 10000, "%.1f%%"))
        {
            rule.MinimumMarginPercent = (decimal)Math.Max(0, margin);
            changed = true;
        }
        var absoluteTolerance = rule.AbsoluteTolerance;
        if (InputUInt("Absolute tolerance", ref absoluteTolerance, 0, 1_000_000))
        {
            rule.AbsoluteTolerance = absoluteTolerance;
            changed = true;
        }

        var tolerance = (float)rule.PercentageTolerance;
        if (ImGui.DragFloat("Percentage tolerance", ref tolerance, 0.05f, 0, 100, "%.2f%%"))
        {
            rule.PercentageTolerance = (decimal)Math.Clamp(tolerance, 0, 100);
            changed = true;
        }

        var warThreshold = (float)rule.PriceWarDropPercent;
        if (ImGui.DragFloat("Price-war drop", ref warThreshold, 0.5f, 0, 99.9f, "%.1f%%"))
        {
            rule.PriceWarDropPercent = (decimal)Math.Clamp(warThreshold, 0, 99.9f);
            changed = true;
        }
        var priceWarAction = rule.PriceWarAction;
        if (DrawEnumCombo("Price-war action", ref priceWarAction))
        {
            rule.PriceWarAction = priceWarAction;
            changed = true;
        }
        var rounding = rule.Rounding;
        if (DrawEnumCombo("Price rounding", ref rounding))
        {
            rule.Rounding = rounding;
            changed = true;
        }
        var qualityFilter = rule.QualityFilter;
        if (DrawEnumCombo("HQ / NQ filter", ref qualityFilter))
        {
            rule.QualityFilter = qualityFilter;
            changed = true;
        }
        ImGui.PopID();
        return changed;
    }

    private void DrawSafetySettings()
    {
        var config = configuration.Current;
        var minimumDelay = config.MinimumDelayMs;
        if (InputInt("Minimum action delay (ms)", ref minimumDelay, 100, 60_000))
        {
            config.MinimumDelayMs = minimumDelay;
            configurationDirty = true;
        }
        var maximumDelay = config.MaximumDelayMs;
        if (InputInt("Maximum action delay (ms)", ref maximumDelay, config.MinimumDelayMs, 60_000))
        {
            config.MaximumDelayMs = maximumDelay;
            configurationDirty = true;
        }
        var requestTimeout = config.MarketRequestTimeoutSeconds;
        if (InputInt("Market timeout (seconds)", ref requestTimeout, 2, 60))
        {
            config.MarketRequestTimeoutSeconds = requestTimeout;
            configurationDirty = true;
        }
        var requestCooldown = config.MarketRequestCooldownMs;
        if (InputInt("Different-item market cooldown (ms)", ref requestCooldown, 1000, 10_000))
        {
            config.MarketRequestCooldownMs = requestCooldown;
            configurationDirty = true;
        }
        var sameItemCacheSeconds = config.SameItemCacheSeconds;
        if (InputInt("Same-item price reuse (seconds)", ref sameItemCacheSeconds, 1, 300))
        {
            config.SameItemCacheSeconds = sameItemCacheSeconds;
            configurationDirty = true;
        }
        var maximumUpdates = config.MaximumUpdatesPerSession;
        if (InputInt("Maximum updates per session", ref maximumUpdates, 1, 200))
        {
            config.MaximumUpdatesPerSession = maximumUpdates;
            configurationDirty = true;
        }

        var openDashboard = config.OpenDashboardOnRetainer;
        if (ImGui.Checkbox("Open dashboard with summoning bell / retainer sell list", ref openDashboard))
        {
            config.OpenDashboardOnRetainer = openDashboard;
            configurationDirty = true;
        }

        if (ImGui.Button("Cancel pending live market request"))
            marketData.ClearCache();
        ImGui.Spacing();
        ImGui.TextWrapped("Consecutive listings of the same item reuse one fresh Compare Prices result. Different items respect the market cooldown before another request. Any logout, player movement, unexpected UI state, changed listing, malformed inventory, or failed server confirmation halts or skips work before another write is attempted.");
        DrawSaveButton();
    }

    private void DrawLog()
    {
        if (ImGui.Button("Copy visible log"))
        {
            var text = string.Join(Environment.NewLine, log.Snapshot().Select(FormatLogEntry));
            ImGui.SetClipboardText(text);
        }
        ImGui.Separator();
        ImGui.BeginChild("AuditLogScroll", Vector2.Zero, true);
        foreach (var entry in log.Snapshot())
        {
            var color = entry.Level switch
            {
                AutomationLogLevel.Warning => new Vector4(1f, 0.72f, 0.2f, 1f),
                AutomationLogLevel.Error => new Vector4(1f, 0.35f, 0.3f, 1f),
                AutomationLogLevel.Debug => new Vector4(0.6f, 0.6f, 0.6f, 1f),
                _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
            };
            ImGui.TextColored(color, FormatLogEntry(entry));
        }
        ImGui.EndChild();
    }

    private void DrawSaveButton()
    {
        if (!configurationDirty)
            return;
        ImGui.Spacing();
        if (ImGui.Button("Save configuration"))
            SaveConfiguration();
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), "Unsaved changes");
    }

    private void SaveConfiguration()
    {
        configuration.Save();
        configurationDirty = false;
    }

    private static bool DrawEnumCombo<T>(string label, ref T value) where T : struct, Enum
    {
        var changed = false;
        if (!ImGui.BeginCombo(label, value.ToString()))
            return false;
        foreach (var option in Enum.GetValues<T>())
        {
            var selected = EqualityComparer<T>.Default.Equals(value, option);
            if (ImGui.Selectable(option.ToString(), selected))
            {
                value = option;
                changed = true;
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
        return changed;
    }

    private static bool InputUInt(string label, ref uint value, uint minimum, uint maximum)
    {
        var integer = (int)Math.Min(value, int.MaxValue);
        if (!ImGui.InputInt(label, ref integer))
            return false;
        value = (uint)Math.Clamp((long)integer, minimum, maximum);
        return true;
    }

    private static bool InputInt(string label, ref int value, int minimum, int maximum)
    {
        if (!ImGui.InputInt(label, ref value))
            return false;
        value = Math.Clamp(value, minimum, maximum);
        return true;
    }

    private static string FormatLogEntry(AutomationLogEntry entry) =>
        $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}";
}
