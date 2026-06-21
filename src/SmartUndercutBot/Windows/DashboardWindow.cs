using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;

namespace SmartUndercutBot.Windows;

public sealed class DashboardWindow : Window
{
    private readonly ConfigurationService configuration;
    private readonly AutomationController automation;
    private readonly ProcurementController procurement;
    private readonly BagListingController bagListing;
    private readonly IUniversalisService universalis;
    private readonly ProcurementLedger procurementLedger;
    private readonly IMarketDataService marketData;
    private readonly AutomationLog log;
    private int newItemId;
    private uint? selectedItemId;
    private int newProcurementItemId;
    private bool configurationDirty;

    public DashboardWindow(
        ConfigurationService configuration,
        AutomationController automation,
        ProcurementController procurement,
        BagListingController bagListing,
        IUniversalisService universalis,
        ProcurementLedger procurementLedger,
        IMarketDataService marketData,
        AutomationLog log)
        : base("Smart Undercutter##Dashboard")
    {
        this.configuration = configuration;
        this.automation = automation;
        this.procurement = procurement;
        this.bagListing = bagListing;
        this.universalis = universalis;
        this.procurementLedger = procurementLedger;
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
        if (ImGui.BeginTabItem("Portfolio"))
        {
            DrawPortfolio();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Bag Listing"))
        {
            DrawBagListing();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Pricing Rules"))
        {
            DrawRules();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Procurement"))
        {
            DrawProcurement();
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
        {
            automation.Halt();
            procurement.Halt();
        }

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

    private void DrawPortfolio()
    {
        var portfolio = automation.PortfolioSnapshot();
        if (!portfolio.StartedAt.HasValue)
        {
            ImGui.TextDisabled("Run a retainer scan to build a listing and gil estimate.");
            return;
        }

        var statusText = portfolio.IsComplete
            ? portfolio.IsFullBellRun
                ? $"Complete account estimate: {portfolio.RetainersScanned} / {portfolio.ExpectedRetainers} retainers"
                : "Complete estimate for the currently open retainer only"
            : $"Scanning: {portfolio.RetainersScanned} / {portfolio.ExpectedRetainers} retainers read";
        ImGui.TextColored(
            portfolio.IsComplete && portfolio.IsFullBellRun
                ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
                : new Vector4(1f, 0.72f, 0.2f, 1f),
            statusText);
        if (portfolio.CompletedAt is { } completed)
            ImGui.TextDisabled($"Last completed {completed.LocalDateTime:g}");

        ImGui.Separator();
        ImGui.TextUnformatted($"Current gil found: {FormatGil(portfolio.CurrentGil)}");
        ImGui.TextDisabled($"Wallet {FormatGil(portfolio.PlayerGil)} + scanned retainers {FormatGil(portfolio.RetainerGil)}");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Listed value at current asking prices: {FormatGil(portfolio.GrossAskingValue)}");
        ImGui.TextUnformatted($"Market-aligned listed estimate: {FormatGil(portfolio.EstimatedGrossValue)}");
        ImGui.TextUnformatted($"Possible repricing markdown: {FormatGil(portfolio.EstimatedMarkdown)}");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Net proceeds at asking prices: {FormatGil(portfolio.EstimatedNetAtAsking)}");
        ImGui.TextUnformatted($"Net market-aligned proceeds: {FormatGil(portfolio.EstimatedNetMarketAligned)}");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f),
            $"Projected total wealth at asking: {FormatGil(portfolio.ProjectedWealthAtAsking)}");
        ImGui.TextColored(new Vector4(0.55f, 0.9f, 0.65f, 1f),
            $"Conservative projected total wealth: {FormatGil(portfolio.ProjectedWealthMarketAligned)}");

        ImGui.Spacing();
        ImGui.TextWrapped(
            $"The market-aligned estimate uses live competitor/strategy prices for {portfolio.LiveEstimatedListings} of {portfolio.Listings} listings and falls back to the current asking price where no live result was available. " +
            "Price-war outliers use the protected historical floor instead of assuming you must match a suspiciously cheap listing. Net values subtract each retainer's seller tax read from the Adjust Price window (usually 5%, 3%, or 0%; 5% fallback). These are estimates, not guaranteed sale proceeds.");

        ImGui.Separator();
        if (!ImGui.BeginTable("PortfolioByRetainer", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX,
                new Vector2(0, 190 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("Retainer");
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Asking", ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Market estimate", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Net estimate", ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Retainer gil", ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Seller fee", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();
        foreach (var retainer in portfolio.Retainers)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.RetainerName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.Listings.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.Units.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.GrossAskingValue.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.EstimatedGrossValue.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.EstimatedNetMarketAligned.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(retainer.RetainerGil.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{retainer.SellerFeePercent:0.##}%");
        }
        ImGui.EndTable();
    }

    private static string FormatGil(ulong value) => $"{value:N0} gil";

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

    private void DrawBagListing()
    {
        var status = bagListing.Status;
        var statusColor = status.State switch
        {
            BagListingState.Failed => new Vector4(1f, 0.35f, 0.3f, 1f),
            BagListingState.Completed => new Vector4(0.35f, 0.9f, 0.45f, 1f),
            BagListingState.ScanningPrices or BagListingState.QueuePrepared or BagListingState.WaitingForVerification =>
                new Vector4(0.35f, 0.75f, 1f, 1f),
            _ => new Vector4(0.75f, 0.75f, 0.75f, 1f),
        };
        ImGui.TextColored(statusColor, status.State.ToString());
        ImGui.SameLine();
        ImGui.TextWrapped(status.Detail);

        ImGui.TextWrapped(
            "Eligible stock is intentionally limited to HQ Grade 4 gemdraughts and HQ Caramel Popcorn. " +
            "Every sale uses a 99-stack; other bag items are ignored.");

        var automatic = configuration.Current.AutomaticCuratedBagListingEnabled;
        if (ImGui.Checkbox("Automatically refill empty retainer slots during idle bell runs", ref automatic))
        {
            configuration.Current.AutomaticCuratedBagListingEnabled = automatic;
            configurationDirty = true;
        }
        var reserve = configuration.Current.BagListingReservePerItem;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (InputInt("Keep in bags per item", ref reserve, 0, 9_999))
        {
            configuration.Current.BagListingReservePerItem = reserve;
            configurationDirty = true;
            bagListing.Refresh();
        }
        ImGui.TextDisabled("Default: keep 100 of each item for personal use. The reserve is checked again before every listing.");

        if (ImGui.Button("Refresh curated stock"))
            bagListing.Refresh();
        ImGui.SameLine();
        var cannotRun = bagListing.IsBusy || automation.IsActive || procurement.IsActive || !bagListing.IsRetainerListOpen;
        ImGui.BeginDisabled(cannotRun);
        if (ImGui.Button("Auto-price and list all eligible 99-stacks"))
            bagListing.PrepareAutomaticRun();
        ImGui.EndDisabled();

        if (!bagListing.IsRetainerListOpen)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                "Open the main summoning-bell retainer list to run across every available retainer.");
        else if (automation.IsActive || procurement.IsActive)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                "The current retainer/procurement operation must finish first.");

        var stock = bagListing.Stock;
        if (ImGui.BeginTable("CuratedBagStock", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, 220 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Bag total", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("List", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("Price floor", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("Auto price", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();
            foreach (var item in stock)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted("HQ");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.TotalQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.ReservedQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{item.StackCount} x99");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.EffectiveFloor.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.SuggestedPrice is not { } price
                    ? "scan needed"
                    : price == PricingStrategyService.MaximumListingPrice ? "live check" : $"{price:N0}");
            }
            ImGui.EndTable();
        }
    }

    private void DrawProcurement()
    {
        var status = procurement.Status;
        var statusColor = status.State switch
        {
            ProcurementState.Faulted or ProcurementState.Halted => new Vector4(1f, 0.35f, 0.3f, 1f),
            ProcurementState.Completed or ProcurementState.PlanReady => new Vector4(0.35f, 0.9f, 0.45f, 1f),
            _ => new Vector4(0.35f, 0.75f, 1f, 1f),
        };
        ImGui.TextColored(statusColor, status.State.ToString());
        ImGui.SameLine();
        ImGui.TextWrapped(status.Detail);
        ImGui.Text($"Purchases: {status.CurrentOrder} / {status.TotalOrders}    Spent: {status.GilSpent:N0} gil");
        if (status.NextAutomaticScan is { } nextScan)
            ImGui.Text($"Next automatic procurement scan: {nextScan.LocalDateTime:g}");

        if (ImGui.Button("Scan Universalis"))
            procurement.ScanNow();
        ImGui.SameLine();
        if (ImGui.Button("Run guarded purchase plan"))
            procurement.RunNow();
        ImGui.SameLine();
        if (ImGui.Button("Run guided deal route"))
            procurement.RunGuidedNow();
        ImGui.SameLine();
        if (ImGui.Button("Stop procurement"))
            procurement.Halt();

        ImGui.Separator();
        var config = configuration.Current;
        var fullLoop = config.AutomationEnabled && config.ProcessAllRetainers && config.RepeatBellRuns &&
                       config.AllowAutomaticWrites && config.AutomaticProcurementEnabled &&
                       config.AllowAutomaticPurchases && config.AllowAutomaticListing;
        if (ImGui.Checkbox("Enable complete AFK reprice + restock loop", ref fullLoop))
        {
            config.AutomationEnabled = fullLoop;
            config.ProcessAllRetainers = fullLoop;
            config.RepeatBellRuns = fullLoop;
            config.AllowAutomaticWrites = fullLoop;
            config.AutomaticProcurementEnabled = fullLoop;
            config.AllowAutomaticPurchases = fullLoop;
            config.AllowAutomaticListing = fullLoop;
            SaveConfiguration();
        }
        ImGui.TextWrapped("When enabled, remain idle with the summoning-bell retainer list open. The plugin reprices all available retainers, detects newly empty sale slots, scans guarded Universalis deals, travels with Lifestream, walks with vnavmesh, buys only after live revalidation, returns home, and lists the purchased stock.");
        var automatic = config.AutomaticProcurementEnabled;
        if (ImGui.Checkbox("Run procurement automatically while idle at the bell", ref automatic))
        {
            config.AutomaticProcurementEnabled = automatic;
            SaveConfiguration();
        }
        var armed = config.AllowAutomaticPurchases;
        if (ImGui.Checkbox("Arm automatic market-board purchases", ref armed))
        {
            config.AllowAutomaticPurchases = armed;
            SaveConfiguration();
        }
        var autoList = config.AllowAutomaticListing;
        if (ImGui.Checkbox("Automatically list purchased stacks on retainers", ref autoList))
        {
            config.AllowAutomaticListing = autoList;
            SaveConfiguration();
        }
        ImGui.TextColored(armed ? new Vector4(1f, 0.72f, 0.2f, 1f) : new Vector4(0.55f, 0.85f, 0.65f, 1f),
            armed
                ? "PURCHASES ARMED: every order is still revalidated against the live in-game listing and price ceiling."
                : "DRY RUN: Universalis plans are shown but no purchases are submitted.");

        var budget = config.ProcurementBudget;
        if (InputUInt("Maximum gil budget", ref budget, 1_000, 100_000_000))
        {
            config.ProcurementBudget = budget;
            configurationDirty = true;
        }
        var interval = config.ProcurementIntervalMinutes;
        if (InputInt("Minutes between procurement scans", ref interval, 5, 1_440))
        {
            config.ProcurementIntervalMinutes = interval;
            configurationDirty = true;
        }
        var targetSlots = config.ProcurementTargetSaleSlots;
        if (InputInt("Maximum sale slots to fill", ref targetSlots, 1, 200))
        {
            config.ProcurementTargetSaleSlots = targetSlots;
            configurationDirty = true;
        }
        var reserveSlots = config.ProcurementInventoryReserve;
        if (InputInt("Bag slots to keep free", ref reserveSlots, 1, 100))
        {
            config.ProcurementInventoryReserve = reserveSlots;
            configurationDirty = true;
        }
        var roi = (float)config.ProcurementMinimumRoiPercent;
        if (ImGui.DragFloat("Minimum expected ROI %", ref roi, 0.5f, 0, 1_000, "%.1f%%"))
        {
            config.ProcurementMinimumRoiPercent = (decimal)Math.Max(0, roi);
            configurationDirty = true;
        }
        var minimumProfit = config.ProcurementMinimumProfitPerUnit;
        if (InputUInt("Minimum profit per unit", ref minimumProfit, 0, 100_000_000))
        {
            config.ProcurementMinimumProfitPerUnit = minimumProfit;
            configurationDirty = true;
        }
        var dataCenter = config.ProcurementDataCenter;
        if (ImGui.InputText("Universalis data center (blank = home DC)", ref dataCenter, 64))
        {
            config.ProcurementDataCenter = dataCenter;
            configurationDirty = true;
        }
        var travelCommand = config.MarketBoardTravelCommand;
        if (ImGui.InputText("Lifestream market-board command", ref travelCommand, 128))
        {
            config.MarketBoardTravelCommand = travelCommand;
            configurationDirty = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Items");
        if (ImGui.Button("Add Grade 4 gemdraughts + Caramel Popcorn"))
        {
            foreach (var rule in universalis.CreateFavoriteRules())
            {
                if (config.ProcurementRules.All(x => x.ItemId != rule.ItemId))
                    config.ProcurementRules.Add(rule);
            }
            configurationDirty = true;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Item ID##Procurement", ref newProcurementItemId);
        ImGui.SameLine();
        if (ImGui.Button("Add item") && newProcurementItemId > 0 &&
            config.ProcurementRules.All(x => x.ItemId != (uint)newProcurementItemId))
        {
            config.ProcurementRules.Add(new ProcurementRule
            {
                ItemId = (uint)newProcurementItemId,
                ItemName = $"Item #{newProcurementItemId}",
            });
            configurationDirty = true;
        }

        if (ImGui.BeginTable("ProcurementRules", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable,
                new Vector2(0, 190 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 35);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 210);
            ImGui.TableSetupColumn("Max unit", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Max slots", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("Weekly sales", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("HQ only", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 45);
            ImGui.TableHeadersRow();
            var removeIndex = -1;
            for (var index = 0; index < config.ProcurementRules.Count; index++)
            {
                var rule = config.ProcurementRules[index];
                ImGui.PushID($"proc-rule-{rule.ItemId}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = rule.Enabled;
                if (ImGui.Checkbox("##enabled", ref enabled)) { rule.Enabled = enabled; configurationDirty = true; }
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{rule.ItemName} ({rule.ItemId})");
                ImGui.TableNextColumn();
                var maxPrice = rule.MaximumUnitPrice;
                ImGui.SetNextItemWidth(-1);
                if (InputUInt("##max", ref maxPrice, 0, 100_000_000)) { rule.MaximumUnitPrice = maxPrice; configurationDirty = true; }
                ImGui.TableNextColumn();
                var stack = rule.TargetStackSize;
                ImGui.SetNextItemWidth(-1);
                if (InputInt("##stack", ref stack, 1, 999)) { rule.TargetStackSize = stack; configurationDirty = true; }
                ImGui.TableNextColumn();
                var maxSlots = rule.MaximumSaleSlots;
                ImGui.SetNextItemWidth(-1);
                if (InputInt("##slots", ref maxSlots, 1, 60)) { rule.MaximumSaleSlots = maxSlots; configurationDirty = true; }
                ImGui.TableNextColumn();
                var velocity = rule.MinimumWeeklyUnitsSold;
                ImGui.SetNextItemWidth(-1);
                if (InputInt("##velocity", ref velocity, 0, 1_000_000)) { rule.MinimumWeeklyUnitsSold = velocity; configurationDirty = true; }
                ImGui.TableNextColumn();
                var hqOnly = rule.RequireHighQuality;
                if (ImGui.Checkbox("##hq", ref hqOnly))
                {
                    rule.RequireHighQuality = hqOnly;
                    if (hqOnly)
                        rule.AllowHighQuality = true;
                    configurationDirty = true;
                }
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("X")) removeIndex = index;
                ImGui.PopID();
            }
            if (removeIndex >= 0)
            {
                config.ProcurementRules.RemoveAt(removeIndex);
                configurationDirty = true;
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Current plan");
        var plan = procurement.Plan;
        ImGui.Text($"{plan.Orders.Count} stack(s) | Cost {plan.TotalCost:N0} | Expected profit {plan.ExpectedProfit:N0} | Sale slots {plan.SaleSlots}");
        if (ImGui.BeginTable("ProcurementPlan", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 140 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Buy/unit");
            ImGui.TableSetupColumn("Ceiling");
            ImGui.TableSetupColumn("Target sale");
            ImGui.TableHeadersRow();
            foreach (var order in plan.Orders)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.WorldName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.ItemName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.Quantity.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.PricePerUnit.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.MaximumAcceptableUnitPrice.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.TargetSalePrice.ToString("N0"));
            }
            ImGui.EndTable();
        }

        var ledger = procurementLedger.Snapshot();
        if (ledger.Count > 0)
        {
            ImGui.TextUnformatted("Purchased inventory waiting to be listed:");
            foreach (var entry in ledger.Where(x => x.PendingQuantity > 0))
                ImGui.BulletText($"{entry.ItemName}: {entry.PendingQuantity} remaining at initial {entry.TargetSalePrice:N0} gil");
        }
        DrawSaveButton();
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
        var retryCount = config.MarketRequestRetryCount;
        if (InputInt("Market request retries", ref retryCount, 0, 5))
        {
            config.MarketRequestRetryCount = retryCount;
            configurationDirty = true;
        }
        var retryBackoff = config.MarketRetryBackoffMs;
        if (InputInt("Market retry backoff (ms)", ref retryBackoff, 1000, 10_000))
        {
            config.MarketRetryBackoffMs = retryBackoff;
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
        ImGui.TextWrapped("A fresh Compare Prices result is reused for matching same-item rows for up to 30 seconds. Distinct items use the configured cooldown, and transient market-loading failures retry the same row with backoff. Any logout, player movement, unexpected UI state, changed listing, malformed inventory, or failed server confirmation stops or skips work before another write is attempted.");
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
