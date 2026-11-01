using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Windows;

/// <summary>
/// The Market tab: what the price memory knows. Our own sales, where each traded
/// line is cheapest right now, and what sells on the home world. Everything here
/// is information; nothing on this tab can buy or price anything.
/// </summary>
public sealed partial class DashboardWindow
{
    private readonly PriceMemoryService priceMemory;
    private readonly MarketScanService marketScan;

    private void DrawMarket()
    {
        var config = configuration.Current;
        var memory = priceMemory.Memory;
        var now = DateTimeOffset.UtcNow;

        ImGui.TextWrapped(marketScan.IsScanning ? "Scanning markets..." : marketScan.Status);
        ImGui.TextDisabled(
            $"Memory: {memory.WorldSnapshotCount:N0} world reading(s), {memory.HomeSnapshotCount:N0} home reading(s), " +
            $"{memory.SaleCount:N0} recorded sale(s). " +
            (priceMemory.LastSavedAt is { } saved ? $"Saved {Age(saved, now)} ago. " : string.Empty) +
            (marketScan.LastTrackedScanAt is { } tracked ? $"Prices scanned {Age(tracked, now)} ago. " : string.Empty) +
            (memory.CandidatesObservedAt is { } ranked ? $"Sales ranking {Age(ranked, now)} old." : string.Empty));
        if (ImGui.Button("Scan now") && !marketScan.IsScanning)
            marketScan.Update(force: true);
        ImGui.SameLine();
        var scanEnabled = config.MarketScanEnabled;
        if (ImGui.Checkbox("Scan automatically", ref scanEnabled))
        {
            config.MarketScanEnabled = scanEnabled;
            configurationDirty = true;
        }
        ImGui.SameLine();
        var interval = config.MarketScanIntervalMinutes;
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if (InputInt("min between price scans", ref interval, 10, 240))
        {
            config.MarketScanIntervalMinutes = interval;
            configurationDirty = true;
        }
        var tracking = config.SaleTrackingEnabled;
        if (ImGui.Checkbox("Record sales from the game's sale messages", ref tracking))
        {
            config.SaleTrackingEnabled = tracking;
            configurationDirty = true;
        }
        ImGui.SameLine();
        var relist = config.RelistAfterSale;
        if (ImGui.Checkbox("Refill a sold slot within a minute", ref relist))
        {
            config.RelistAfterSale = relist;
            configurationDirty = true;
        }

        if (ImGui.CollapsingHeader("Your sales", ImGuiTreeNodeFlags.DefaultOpen))
            DrawOwnSales(memory, now);
        if (ImGui.CollapsingHeader("Where your items are cheapest", ImGuiTreeNodeFlags.DefaultOpen))
            DrawCheapestWorlds(memory, now);
        if (ImGui.CollapsingHeader("What sells on your home world"))
            DrawCandidates(memory, now);
    }

    private void DrawOwnSales(PriceMemory memory, DateTimeOffset now)
    {
        var day = memory.SummarizeSales(now, TimeSpan.FromDays(1));
        var week = memory.SummarizeSales(now, TimeSpan.FromDays(7));
        ImGui.TextUnformatted(
            $"Last 24 hours: {day.Sum(x => x.Sales):N0} sale(s), {day.Aggregate(0UL, (s, x) => s + x.GilAfterFees):N0} gil after fees. " +
            $"Last 7 days: {week.Sum(x => x.Sales):N0} sale(s), {week.Aggregate(0UL, (s, x) => s + x.GilAfterFees):N0} gil.");
        if (week.Count == 0)
        {
            ImGui.TextDisabled("No sales recorded yet. Sales are read from the game's own \"has sold\" message while you are logged in.");
            return;
        }
        if (!ImGui.BeginTable("OwnSales", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupColumn("Item (7 days)");
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Gil after fees", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Per unit", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Last sold", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var row in week)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.ItemName}{(row.IsHighQuality ? " HQ" : string.Empty)}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Sales:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Units:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.GilAfterFees:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Units == 0 ? "-" : $"{row.GilAfterFees / row.Units:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Age(row.LastSaleAt, now)} ago");
        }
        ImGui.EndTable();
    }

    private void DrawCheapestWorlds(PriceMemory memory, DateTimeOffset now)
    {
        var config = configuration.Current;
        var lines = MarketScanService.TrackedItems(config);
        if (lines.Count == 0)
        {
            ImGui.TextDisabled("No traded lines are enabled.");
            return;
        }
        ImGui.TextDisabled("Cheapest remembered listings on other worlds (live reads and the regional scan), " +
                           "with units priced at least 15% under the home floor after both taxes. Routing uses this; buying always re-reads the board.");
        if (!ImGui.BeginTable("CheapestWorlds", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, 260 * ImGuiHelpers.GlobalScale)))
            return;
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Home floor", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Home units/day", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Best worlds (lowest, units at 15%+ margin, age)");
        ImGui.TableSetupColumn("Buyable units", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Home age", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        var fees = config.Fees;
        foreach (var (itemId, quality) in lines)
        {
            var name = config.ProcurementRules.FirstOrDefault(x => x.ItemId == itemId)?.ItemName ?? $"#{itemId}";
            var home = memory.LatestHome(itemId, quality, now, TimeSpan.FromDays(2));
            var worlds = memory.FreshWorlds(itemId, quality, now, TimeSpan.FromHours(6));
            // The price at which a unit still clears a 15% margin after both taxes.
            var ceiling = home is { LowestCompetitor: > 0 }
                ? (uint)decimal.Floor(fees.NetUnitProceeds(home.LowestCompetitor) / (1m + fees.BuyerFeePercent / 100m) / 1.15m)
                : 0u;
            var best = worlds.Take(3).Select(w =>
                $"{w.World} {w.Lowest:N0}" + (ceiling > 0 ? $" ({w.UnitsAtOrBelow(ceiling):N0}u)" : string.Empty) +
                $" {Age(w.ObservedAt, now)}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{name}{(quality ? " HQ" : string.Empty)}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(home is { LowestCompetitor: > 0 } ? $"{home.LowestCompetitor:N0}" : "-");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(home is null ? "-" : $"{home.UnitsPerDay:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(worlds.Count == 0 ? "not scanned yet" : string.Join(" | ", best));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(ceiling == 0 ? "-"
                : $"{worlds.Aggregate(0UL, (sum, w) => sum + w.UnitsAtOrBelow(ceiling)):N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(home is null ? "-" : Age(home.ObservedAt, now));
        }
        ImGui.EndTable();
    }

    private void DrawCandidates(PriceMemory memory, DateTimeOffset now)
    {
        var candidates = memory.Candidates;
        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("The daily sales ranking has not run yet. It asks Saddlebag Exchange what sold on your home world this week.");
            return;
        }
        var config = configuration.Current;
        ImGui.TextDisabled("Home-world markets ranked by gil traded per day (Saddlebag, last 7 days), with the cheapest cached " +
                           "listing elsewhere (Universalis). Margin is after both taxes at the cheapest cached price, which may be a single unit.");
        if (!ImGui.BeginTable("MarketCandidates", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, 320 * ImGuiHelpers.GlobalScale)))
            return;
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Units/day", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Sales/day", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Per sale", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Home price", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cheapest elsewhere");
        ImGui.TableSetupColumn("Margin", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Trend / traded", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var candidate in candidates)
        {
            var rule = config.ProcurementRules.FirstOrDefault(x => x.ItemId == candidate.ItemId);
            var traded = rule is null ? "not traded"
                : rule.LiquidateOnly ? "sell-off only"
                : rule.PreferredStock ? "core" : rule.Enabled ? "traded" : "disabled";
            var roi = candidate.NetRoiPercent(config.Fees.MarketTaxPercent, config.Fees.BuyerFeePercent);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{candidate.ItemName}{(candidate.IsHighQuality ? " (HQ)" : string.Empty)}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(candidate.Category);
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{candidate.UnitsPerDay:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{candidate.SalesPerDay:N1}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{candidate.UnitsPerSale:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{candidate.HomePrice:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(candidate.CheapestElsewhere == 0 ? "-"
                : $"{candidate.CheapestElsewhere:N0} on {candidate.CheapestWorld}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(roi is { } r ? $"{r:N0}%" : "-");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{candidate.Trend} / {traded}");
        }
        ImGui.EndTable();
    }

    private static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return age.TotalMinutes < 1 ? "now"
            : age.TotalHours < 1 ? $"{age.TotalMinutes:N0}m"
            : age.TotalDays < 1 ? $"{age.TotalHours:N0}h"
            : $"{age.TotalDays:N0}d";
    }
}
