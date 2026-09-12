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
    private readonly StockAutomationController stockAutomation;
    private readonly WealthHistoryService wealthHistory;
    private int wealthRangeIndex = 1;
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
        AutomationLog log,
        StockAutomationController stockAutomation,
        WealthHistoryService wealthHistory)
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
        this.stockAutomation = stockAutomation;
        this.wealthHistory = wealthHistory;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 520),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawRunControls();
        ImGui.Separator();
        if (!ImGui.BeginTabBar("DashboardTabs"))
            return;

        if (ImGui.BeginTabItem("Home"))
        {
            DrawHome();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Stock"))
        {
            DrawBagListing();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Shopping"))
        {
            DrawProcurement();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Earnings"))
        {
            DrawPortfolio();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Advanced"))
        {
            DrawAdvanced();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        if (configurationDirty)
            SaveConfiguration();
    }

    private void DrawRunControls()
    {
        var attention = stockAutomation.NeedsAttention;
        var running = configuration.Current.KeepsRetainersStocked;
        ImGui.TextColored(attention ? new Vector4(1f, 0.72f, 0.2f, 1f)
            : running ? new Vector4(0.35f, 0.85f, 0.65f, 1f) : new Vector4(0.75f, 0.75f, 0.75f, 1f),
            attention ? "Paused - check the message below"
                : running ? "All automation: ON" : "All automation: OFF");
        ImGui.BeginDisabled(stockAutomation.StartIssue is not null);
        if (ImGui.Button("Start all automation"))
            stockAutomation.Start();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Stop all automation"))
            stockAutomation.Stop();
        if (!stockAutomation.IsBusy && stockAutomation.StartIssue is { } issue)
            ImGui.TextWrapped(issue);
    }

    private void DrawHome()
    {
        var config = configuration.Current;
        ImGui.TextWrapped("Check retainers > fill from bags > buy good deals > return home and list > repeat.");
        if (config.PriorityShoppingEnabled)
            ImGui.TextWrapped("Shopping: compare regional prices, then sweep the worlds one data center at a time. " +
                              $"Every stop prices the food and potions first, then rotating flips, up to {config.PriorityItemsPerWorld} items. " +
                              "Compare deals before buying and refresh aging home resale prices. " +
                              "Return home to list and collect, then resume the remaining worlds. Use nearby boards and bells before teleporting.");
        ImGui.TextWrapped("Start enables price changes, automatic purchases, listing, gil collection, and repeat checks. " +
                          "It uses your limits below. Keep the game running and leave the retainer list open between trips.");
        ImGui.Spacing();

        ImGui.TextUnformatted("What is happening");
        var detail = procurement.IsActive ? procurement.Status.Detail
            : automation.IsActive ? automation.Status.Detail
            : bagListing.IsBusy ? bagListing.Status.Detail
            : procurement.RequiresManualRestart || procurement.IsWaitingToReturnHome ? procurement.Status.Detail
            : automation.RequiresManualRestart ? automation.Status.Detail
            : !config.KeepsRetainersStocked ? "Ready when you are. Review your spending limits, open a summoning bell, and press Start."
            : !bagListing.IsRetainerListOpen ? "Waiting for the summoning-bell retainer list to open."
            : procurement.ShoppingWaitReason is { } waitReason ? $"Shopping: {waitReason}. Retainer checks continue."
            : procurement.Status.Detail;
        ImGui.TextWrapped(detail);
        if (automation.LastWriteFailure is { } writeFailure)
            ImGui.TextWrapped($"A price update was refused by the game and that listing is being left alone: {writeFailure}");
        if (stockAutomation.NeedsAttention)
            ImGui.TextWrapped("Resolve the message above, then press Start to resume.");

        var status = automation.Status;
        if (status.TotalRetainers > 0)
        {
            var totalSlots = status.TotalRetainers * 20;
            if (automation.LastKnownFreeSaleSlots is { } free)
            {
                ImGui.Spacing();
                ImGui.ProgressBar((float)(totalSlots - free) / totalSlots, new Vector2(-1, 0),
                    $"Last completed check: {totalSlots - free} / {totalSlots} sale slots filled");
                ImGui.Text($"{free} empty sale slots   |   {procurement.MarketableBagSlots} bag slots holding marketable items   |   {procurementLedger.PendingSaleSlots} queued to list");
            }
        }
        else
            ImGui.TextDisabled("Retainer capacity will appear after the first complete check.");

        if (status.State == AutomationState.WaitingForScheduledRun && status.NextActionAt is { } next)
            ImGui.Text($"Next retainer check: {next.LocalDateTime:t}");
        if (!procurement.IsActive && procurement.Status.NextAutomaticScan is { } scan)
            ImGui.Text(scan <= DateTimeOffset.UtcNow
                ? "Deal search: ready after the retainer check and bag refill."
                : $"Next deal search / retry: {scan.LocalDateTime:t}");
        DrawPortfolioSummary();
        ImGui.TextWrapped("Sold slots are detected on the next retainer check. An empty slot is better than a slot of low-value stock, so slots stay open when nothing good qualifies.");
        if (config.ContinueShoppingWhenStocked)
            ImGui.TextWrapped($"Trading stock in bags: {procurement.ResaleBagSlots} planned sale stack(s), aiming for {procurement.ComfortableStockTarget}. " +
                "One sale stack uses that item's selling quantity, such as 99 potions or 20 dyes. Personal reserves and sale-only items are excluded.");
        ImGui.Spacing();
        DrawLoopStages();
        ImGui.Separator();

        ImGui.TextUnformatted("Your limits (saved automatically)");
        DrawSpendingLimits();
        var roi = (float)config.ProcurementMinimumRoiPercent;
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragFloat("Minimum expected return after fees", ref roi, 0.5f, 0, 1_000, "%.1f%%"))
        {
            config.ProcurementMinimumRoiPercent = (decimal)Math.Max(0, roi);
            configurationDirty = true;
        }
        var reserve = config.BagListingReservePerItem;
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if (InputInt("Keep in bags per item", ref reserve, 0, 9_999))
        {
            config.BagListingReservePerItem = reserve;
            configurationDirty = true;
        }
        ImGui.TextWrapped($"Bag refills list HQ Grade 4 gemdraughts and HQ Caramel Popcorn in complete 99-stacks, keeping {reserve:N0} of each, " +
                          "plus high-volume dyes. Materia, ethers and the remaining dyes are sold off with nothing kept back. " +
                          "See Stock for exactly what each bag item will do, and Shopping for the item list and limits.");
        ImGui.TextWrapped($"At home: check retainers every {config.RepeatMinimumMinutes}-{config.RepeatMaximumMinutes} minutes; retry deals every {config.ProcurementIntervalMinutes} minutes. Shopping pauses these checks until the return trip.");
        ImGui.TextWrapped("Travel requires Lifestream and vnavmesh. Live prices and inventory confirmation are checked before another purchase is attempted.");
    }

    // One compact line: is the portfolio the shape it is meant to be? Preferred
    // stock should hold most of it, and opportunistic arbitrage should be a sliver.
    private void DrawPortfolioSummary()
    {
        var portfolio = procurement.PortfolioSummary;
        if (portfolio.CapacitySlots == 0)
            return;
        var short_ = portfolio.CoreDeficit > 0;
        var over = portfolio.OpportunisticSlots > portfolio.OpportunisticCap;
        ImGui.TextColored(short_ ? new Vector4(1f, 0.72f, 0.2f, 1f) : new Vector4(0.55f, 0.85f, 0.65f, 1f),
            $"Preferred: {portfolio.CoreSlots} / {portfolio.CoreTarget} target");
        ImGui.SameLine();
        ImGui.TextUnformatted($"   Secondary: {portfolio.SecondarySlots}   ");
        ImGui.SameLine();
        ImGui.TextColored(over ? new Vector4(1f, 0.72f, 0.2f, 1f) : new Vector4(0.55f, 0.85f, 0.65f, 1f),
            $"Opportunistic: {portfolio.OpportunisticSlots} / {portfolio.OpportunisticCap} cap");
        if (over)
            ImGui.TextDisabled("Opportunistic stock is over its cap, so purchases favour preferred stock until it sells down.");
    }

    // Start runs every stage, but when one is idle the reason is invisible - full
    // retainers look identical to shopping being switched off. Show each stage and
    // why it is waiting.
    private void DrawLoopStages()
    {
        var config = configuration.Current;
        var free = automation.LastKnownFreeSaleSlots;
        var pending = procurementLedger.PendingSaleSlots;
        var running = new Vector4(0.35f, 0.75f, 1f, 1f);
        var waiting = new Vector4(0.75f, 0.75f, 0.75f, 1f);
        var blocked = new Vector4(1f, 0.72f, 0.2f, 1f);
        var goal = new Vector4(0.35f, 0.9f, 0.45f, 1f);

        ImGui.TextUnformatted("Everything Start runs");
        if (!ImGui.BeginTable("LoopStages", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        void Stage(string name, Vector4 color, string state)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            ImGui.TextColored(color, state);
        }

        var armed = config.KeepsRetainersStocked;
        var retainerPausedForTravel = procurement.IsActive || procurement.IsWaitingToReturnHome;
        Stage("1. Check retainers, reprice, collect gil",
            automation.IsActive ? running : stockAutomation.NeedsAttention ? blocked : waiting,
            automation.IsActive ? "running now"
                : retainerPausedForTravel ? "resumes after returning home"
                : automation.RequiresManualRestart ? "paused - see the message above"
                : !armed ? "press Start to enable the loop"
                : !bagListing.IsRetainerListOpen ? "waiting for the summoning-bell list"
                : automation.Status.NextActionAt is { } next ? $"next at {next.LocalDateTime:t}"
                : "ready");

        var bagStock = procurement.MarketableBagSlots;
        Stage("2. List stock from your bags",
            bagListing.IsBusy ? running : waiting,
            bagListing.IsBusy ? "running now"
                : bagListing.Status.State == BagListingState.Failed ? bagListing.Status.Detail
                : free is 0 && bagStock > 0 ? $"{bagStock} bag stack(s) to check as sale slots open"
                : pending > 0 ? $"{pending} stack(s) queued"
                : bagStock > 0 ? $"{bagStock} resale stack(s) to check"
                : "no spare resale stock found");

        Stage("3. Travel to other worlds and buy deals",
            procurement.IsActive ? running : procurement.RequiresManualRestart ? blocked : waiting,
            procurement.IsActive ? "running now"
                : procurement.RequiresManualRestart ? "stopped - check the last purchase in game"
                : procurement.IsWaitingToReturnHome ? "waiting to retry the return home"
                : !armed ? "press Start to enable the loop"
                : procurement.ShoppingWaitReason is { } reason ? $"Buying: {reason}"
                : procurement.Status.NextAutomaticScan is { } scan
                    ? scan <= DateTimeOffset.UtcNow ? "ready after retainers and bags" : $"next deal search at {scan.LocalDateTime:t}"
                    : "ready");

        Stage("4. Return home and list what was bought", waiting, "runs at the end of each trip");
        ImGui.EndTable();

        if (free is 0)
            ImGui.TextWrapped(config.ContinueShoppingWhenStocked
                ? $"Retainers are full. Shopping tops up toward {procurement.ComfortableStockTarget} spare sale stacks using the {config.ProcurementBufferGilPercent:0}% budget. Deal searches continue between restocks."
                : $"Retainers are full. Spare stock is limited to {config.ProcurementBagBufferStacks} sale stacks and {config.ProcurementBufferGilPercent:0}% of available capital.");
    }

    private void DrawSpendingLimits()
    {
        var config = configuration.Current;
        var reinvest = config.ReinvestAvailableGil;
        if (ImGui.Checkbox("Reinvest available gil and sale income", ref reinvest))
        {
            config.ReinvestAvailableGil = reinvest;
            configurationDirty = true;
        }
        if (!reinvest)
        {
            var budget = config.ProcurementBudget;
            ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
            if (InputUInt("Maximum gil per trip", ref budget, 1_000, 100_000_000))
            {
                config.ProcurementBudget = budget;
                configurationDirty = true;
            }
            ImGui.TextWrapped("The trip cap resets after returning home. Later trips can reinvest sale income.");
        }
        var reserve = config.ProcurementTravelReserve;
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if (InputUInt("Gil to keep for travel", ref reserve, 0, 100_000_000))
        {
            config.ProcurementTravelReserve = reserve;
            configurationDirty = true;
        }
        var continuedBuying = config.ContinueShoppingWhenStocked;
        if (ImGui.Checkbox("Keep a comfortable stock in bags", ref continuedBuying))
        {
            config.ContinueShoppingWhenStocked = continuedBuying;
            configurationDirty = true;
        }
        if (!continuedBuying)
        {
            var buffer = config.ProcurementBagBufferStacks;
            ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
            if (InputInt("Spare resale stacks to keep in bags", ref buffer, 0, 50))
            {
                config.ProcurementBagBufferStacks = buffer;
                configurationDirty = true;
            }
        }
        var bufferPercent = (float)config.ProcurementBufferGilPercent;
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragFloat("Buffer budget when sale slots are covered", ref bufferPercent, 1f, 0, 100, "%.0f%%"))
        {
            config.ProcurementBufferGilPercent = (decimal)Math.Clamp(bufferPercent, 0f, 100f);
            configurationDirty = true;
        }
        ImGui.TextWrapped("List existing stock first. If preferred food and potions are below their portfolio target, " +
            "hunt deals for a replacement buffer even while other listings occupy the retainers. Preferred deals can use " +
            "available gil after the travel reserve; other spare stock keeps the smaller budget, including its existing purchase cost. " +
            "On a buying visit, sweep preferred fast-mover bargains at the selected price or better, sized by demand and actual bag space. " +
            "Then focus on listing and undercutting until the purchased replacement supply runs low.");
        ImGui.Text($"Available for the next trip: {procurement.ShoppingBudget:N0} gil   |   room for {procurement.PurchaseCapacity} stack(s)");
        ImGui.TextWrapped(procurement.ShoppingStrategy);
        if (procurement.ShoppingWaitReason is { } shoppingWait)
            ImGui.TextWrapped($"Not buying right now: {shoppingWait}.");
    }

    private void DrawAdvanced()
    {
        if (!ImGui.BeginTabBar("AdvancedTabs"))
            return;
        if (ImGui.BeginTabItem("Individual controls"))
        {
            DrawStatus();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Pricing"))
        {
            DrawRules();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Listing queue"))
        {
            DrawQueue();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Timing & data"))
        {
            DrawSafetySettings();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Activity log"))
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

        var collectGil = config.AutomaticallyCollectRetainerGil;
        if (ImGui.Checkbox("Collect gil from every processed retainer", ref collectGil))
        {
            config.AutomaticallyCollectRetainerGil = collectGil;
            SaveConfiguration();
        }
        ImGui.TextDisabled("Withdraws after listings finish, verifies the retainer balance, and respects the player gil cap.");

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
        if (ImGui.Button("Stop all##Advanced"))
            stockAutomation.Stop();

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

    private static readonly (string Label, TimeSpan Window)[] WealthRanges =
    [
        ("24 hours", TimeSpan.FromHours(24)),
        ("7 days", TimeSpan.FromDays(7)),
        ("30 days", TimeSpan.FromDays(30)),
        ("All", TimeSpan.Zero),
    ];

    private void DrawWealthGraph()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = wealthHistory.History.Within(WealthRanges[wealthRangeIndex].Window, now);
        ImGui.Separator();
        ImGui.TextUnformatted("Total wealth over time");
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Range", ref wealthRangeIndex, WealthRanges.Select(x => x.Label).ToArray(), WealthRanges.Length))
            wealthRangeIndex = Math.Clamp(wealthRangeIndex, 0, WealthRanges.Length - 1);
        ImGui.SameLine();
        // Builds before v1.0.0.63 plotted bag-listing passes as gil-only points,
        // which leaves a permanent sawtooth in an existing history.
        if (ImGui.Button("Clear history"))
            wealthHistory.Clear();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Discards every recorded point and starts the series again from the next completed all-retainer check.");

        if (samples.Count < 2)
        {
            ImGui.TextDisabled(wealthHistory.History.Samples.Count == 0
                ? "No points yet. One is recorded each time a complete all-retainer check finishes."
                : "Only one point in this range so far; a line needs at least two.");
            return;
        }

        // Gil totals run into the billions, so plot in millions to keep the axis readable.
        var values = samples.Select(x => (float)(x.Total / 1_000_000.0)).ToArray();
        var lowest = values.Min();
        var highest = values.Max();
        var padding = Math.Max((highest - lowest) * 0.1f, 0.01f);
        var change = wealthHistory.History.ChangeOver(WealthRanges[wealthRangeIndex].Window, now);

        ImGui.PlotLines("##WealthOverTime", values, 0,
            $"{FormatGil(samples[^1].Total)} now",
            lowest - padding, highest + padding,
            new Vector2(-1, 90 * ImGuiHelpers.GlobalScale));

        ImGui.TextDisabled($"{samples.Count} point(s) from {samples[0].At.LocalDateTime:g} " +
                           $"| low {FormatGil((ulong)(lowest * 1_000_000))} " +
                           $"| high {FormatGil((ulong)(highest * 1_000_000))}");
        if (change is { } delta)
        {
            var perDay = delta.Span.TotalDays >= 0.05
                ? $" (about {FormatGil((ulong)Math.Abs(delta.Change / delta.Span.TotalDays))} per day)"
                : string.Empty;
            ImGui.TextColored(
                delta.Change >= 0 ? new Vector4(0.35f, 0.9f, 0.45f, 1f) : new Vector4(1f, 0.45f, 0.4f, 1f),
                $"{(delta.Change >= 0 ? "Up" : "Down")} {FormatGil((ulong)Math.Abs(delta.Change))} " +
                $"over {FormatSpan(delta.Span)}{perDay}");
        }
        ImGui.TextDisabled("Each point is one completed all-retainer check, using the conservative " +
                           "market-aligned wealth estimate. Partial scans are not plotted.");
    }

    private static string FormatSpan(TimeSpan span) => span.TotalDays >= 1
        ? $"{span.TotalDays:N1} day(s)"
        : span.TotalHours >= 1 ? $"{span.TotalHours:N1} hour(s)" : $"{span.TotalMinutes:N0} minute(s)";

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

        DrawWealthGraph();

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
            BagListingState.ConsolidatingBags or BagListingState.ScanningPrices or BagListingState.QueuePrepared or BagListingState.WaitingForVerification =>
                new Vector4(0.35f, 0.75f, 1f, 1f),
            _ => new Vector4(0.75f, 0.75f, 0.75f, 1f),
        };
        ImGui.TextColored(statusColor, status.State.ToString());
        ImGui.SameLine();
        ImGui.TextWrapped(status.Detail);

        ImGui.TextWrapped(
            "Marketable bag items are shown below. Bag slots are physical inventory stacks; units are individual items. " +
            "Sell as shows the planned listing quantities. Enabled trading stock and sell-off items can refill retainers; " +
            "ignored items are left alone.");

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
        ImGui.TextDisabled("Default: keep 100 of each curated food/potion. Dye and sell-off rules have their own reserves.");

        if (ImGui.Button("Refresh bags"))
            bagListing.Refresh();
        ImGui.SameLine();
        var cannotRun = bagListing.IsBusy || automation.IsActive || procurement.IsActive || !bagListing.IsRetainerListOpen;
        ImGui.BeginDisabled(cannotRun);
        if (ImGui.Button("Price and list eligible stock"))
            bagListing.PrepareAutomaticRun();
        ImGui.EndDisabled();

        if (!bagListing.IsRetainerListOpen)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                "Open the main summoning-bell retainer list to run across every available retainer.");
        else if (automation.IsActive || procurement.IsActive)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                "The current retainer/procurement operation must finish first.");

        var stock = bagListing.Stock;
        if (ImGui.BeginTable("CuratedBagStock", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, 220 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Bag slots", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Sell as", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("Price floor", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("Auto price", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();
            foreach (var item in stock)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                if (item.Eligible)
                    ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.45f, 1f), "Sell");
                else
                    ImGui.TextDisabled("Ignored");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.IsHighQuality ? "HQ" : "NQ");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.PhysicalBagSlots.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.TotalQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.ReservedQuantity.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.StackCount == 0
                    ? "-"
                    : $"{item.StackCount} x{Math.Max(1, item.TargetStackSize)}");
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

        ImGui.TextUnformatted("One-time actions");
        ImGui.BeginDisabled(stockAutomation.IsBusy);
        if (ImGui.Button("Find deals"))
            procurement.ScanNow();
        ImGui.SameLine();
        if (ImGui.Button("Buy planned deals"))
            procurement.RunNow();
        ImGui.SameLine();
        if (ImGui.Button("Run guided deal route"))
            procurement.RunGuidedNow();
        if (ImGui.Button("Live all-world stock hunt"))
            procurement.RunLiveStockHuntNow();
        ImGui.EndDisabled();
        ImGui.TextWrapped("The live all-world hunt scans prices on every world first, then makes a separate buying pass. The guided route waits for you to buy manually.");
        if (configuration.Current.PriorityShoppingEnabled)
            ImGui.TextWrapped("Scout first: compare regional prices, then price the curated food and potions on every world - they are " +
                $"never rotated out - plus promising or rotating flips, up to {configuration.Current.PriorityItemsPerWorld} items per world. " +
                "One circuit covers every world, two stops at a time across Aether > Primal > Crystal > Dynamis, so a shortened trip still compares all four. Ordinary deals are compared before a buying pass; " +
                "only deals with at least 100% expected return after fees are bought immediately. All purchases still need live price, demand, stock and budget checks. " +
                $"Next route stop: {((string.IsNullOrEmpty(configuration.Current.PriorityNextWorld)) ? "Aether" : configuration.Current.PriorityNextWorld)}.");
        DrawPortfolioSummary();
        if (ImGui.CollapsingHeader("Why each item was chosen or skipped"))
        {
            ImGui.TextWrapped("Core stock is the curated food and gemdraughts. Secondary is anything not pinned that " +
                              "still shows real value and demand. Opportunistic is dyes, materia and one-off arbitrage, " +
                              "and it is capped so it cannot take over the retainers.");
            var decisions = procurement.PortfolioDecisions;
            if (decisions.Count == 0)
                ImGui.TextDisabled("No decisions yet. They are recorded each time a purchase plan is built.");
            else if (ImGui.BeginTable("portfolio-decisions", 7,
                         ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                         new Vector2(0, 260 * ImGuiHelpers.GlobalScale)))
            {
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Units/day", ImGuiTableColumnFlags.WidthFixed, 75);
                ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 85);
                ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Turnover", ImGuiTableColumnFlags.WidthFixed, 75);
                ImGui.TableSetupColumn("Decision");
                ImGui.TableHeadersRow();
                foreach (var row in decisions)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{row.ItemName}{(row.IsHighQuality ? " HQ" : string.Empty)}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.Tier switch
                    {
                        PortfolioTier.Core => new Vector4(0.35f, 0.9f, 0.45f, 1f),
                        PortfolioTier.Secondary => new Vector4(0.35f, 0.75f, 1f, 1f),
                        _ => new Vector4(0.75f, 0.75f, 0.75f, 1f),
                    }, row.Tier.ToString().ToUpperInvariant());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.SalesPerDay.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.ExpectedProfit.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.RoiPercent:N0}%");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.DaysToSell:N2}d");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.Selected ? new Vector4(0.55f, 0.85f, 0.65f, 1f) : new Vector4(0.8f, 0.8f, 0.8f, 1f),
                        $"{(row.Selected ? "selected" : "skipped")}: {row.Reason}");
                }
                ImGui.EndTable();
            }
        }
        if (ImGui.CollapsingHeader("Home reference prices"))
        {
            var reference = procurement.HomeReferencePrices;
            ImGui.TextWrapped("What every away-world deal is measured against, read live from your home world. " +
                              "A listing under half the median is treated as someone's mistake and ignored, so one " +
                              "wildly cheap row cannot make every deal look unprofitable.");
            if (procurement.HomePricesUpdatedAt is { } updated)
                ImGui.TextDisabled($"Last read {updated.LocalDateTime:g}");
            if (reference.Count == 0)
                ImGui.TextDisabled("No home prices yet. They are read at the start of each shopping route.");
            else if (ImGui.BeginTable("home-reference", 7,
                         ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                         new Vector2(0, 260 * ImGuiHelpers.GlobalScale)))
            {
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Q", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("Units/day", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 65);
                ImGui.TableSetupColumn("Lowest", ImGuiTableColumnFlags.WidthFixed, 85);
                ImGui.TableSetupColumn("Median", ImGuiTableColumnFlags.WidthFixed, 85);
                ImGui.TableSetupColumn("Reference", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();
                foreach (var row in reference)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.ItemName);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.IsHighQuality ? "HQ" : "NQ");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(procurement.HomeSalesPerDay(row.ItemId, row.IsHighQuality).ToString("N1"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Listings.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Lowest.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Median.ToString("N0"));
                    ImGui.TableNextColumn();
                    if (row.Ignored > 0)
                        ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                            $"{row.Reference:N0}  ({row.Ignored} ignored)");
                    else
                        ImGui.TextUnformatted(row.Reference.ToString("N0"));
                }
                ImGui.EndTable();
            }
            ImGui.TextWrapped("Higher home-world sales/day gets shopping priority. Units/day counts quantities sold, with HQ and NQ separate. If Universalis has no rate, recent seven-day sales provide a conservative estimate. Profit and stock limits still apply.");
        }
        if (ImGui.CollapsingHeader("Recent live price comparisons"))
        {
            ImGui.TextWrapped("These checks are also saved to the session log on the Activity log tab. Zero listings means a completed empty server response.");
            if (ImGui.BeginTable("live-comparisons", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                    new Vector2(0, 260 * ImGuiHelpers.GlobalScale)))
            {
                ImGui.TableSetupColumn("Time / world");
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Listings / units");
                ImGui.TableSetupColumn("Lowest gil");
                ImGui.TableSetupColumn("Decision");
                ImGui.TableHeadersRow();
                foreach (var row in procurement.RecentPrices.Reverse().Take(100))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.At.LocalDateTime:t} {row.World}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Item}{(row.HighQuality ? " HQ" : "")}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Listings} / {row.Units:N0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Lowest == 0 ? "-" : row.Lowest.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextWrapped(row.Decision);
                }
                ImGui.EndTable();
            }
        }

        ImGui.Separator();
        var config = configuration.Current;
        if (ImGui.CollapsingHeader("Individual shopping switches"))
        {
            ImGui.TextWrapped("Start on the Home tab sets these for continuous restocking. Use these switches for custom workflows.");
            var priority = config.PriorityShoppingEnabled;
            if (ImGui.Checkbox("Scout across data centers, compare, then buy", ref priority))
            {
                config.PriorityShoppingEnabled = priority;
                SaveConfiguration();
            }
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
        }

        if (ImGui.CollapsingHeader("Shopping limits and travel settings"))
        {
            DrawSpendingLimits();
            var guidedWorlds = config.GuidedTourMaximumWorlds;
            if (InputInt("Maximum worlds per guided route", ref guidedWorlds, 1, 20))
            {
                config.GuidedTourMaximumWorlds = guidedWorlds;
                configurationDirty = true;
            }
            var highQualityOnly = config.BuyHighQualityOnly;
            if (ImGui.Checkbox("Only buy high-quality stock", ref highQualityOnly))
            {
                config.BuyHighQualityOnly = highQualityOnly;
                configurationDirty = true;
            }
            ImGui.TextDisabled(highQualityOnly
                ? "On: buy HQ when an item has an HQ form. NQ-only items such as dyes are still eligible."
                : "Off: normal quality is bought whenever an item rule allows it.");
            var valueTarget = config.ProcurementBufferValueTarget;
            if (InputUInt("Spare stock worth at least (gil)", ref valueTarget, 0, 999_999_999))
            {
                config.ProcurementBufferValueTarget = valueTarget;
                configurationDirty = true;
            }
            ImGui.TextDisabled("Shopping keeps going while the bag buffer is worth less than this, even once the stack count is met - a bag of cheap dye is not a trading position. Set 0 to judge on stack count alone.");
            var fillRoi = (float)config.ProcurementFillRoiPercent;
            if (ImGui.DragFloat("Fill-up ROI % for empty slots", ref fillRoi, 0.5f, 0, 1_000, "%.1f%%"))
            {
                config.ProcurementFillRoiPercent = (decimal)Math.Max(0, fillRoi);
                configurationDirty = true;
            }
            ImGui.TextDisabled("When the plan still leaves sale slots empty, preferred and high-liquidity stock may be taken at this lower margin. Opportunistic stock never can: an empty slot beats a slot of junk.");
            var fastRoi = (float)config.ProcurementFastMoverRoiPercent;
            if (ImGui.DragFloat("High-volume ROI %", ref fastRoi, 0.5f, 0, 1_000, "%.1f%%"))
            {
                config.ProcurementFastMoverRoiPercent = (decimal)Math.Max(0, fastRoi);
                configurationDirty = true;
            }
            ImGui.TextDisabled("The margin required on preferred stock that is both fast-moving and valuable per slot - popcorn, potages, raid gemdraughts. Their return comes from turning the gil over, so holding out for the full margin mostly leaves the gil idle. The per-unit profit floor and every safety check still apply.");
            var highVolumeRoi = (float)config.ProcurementHighVolumeRoiPercent;
            if (ImGui.DragFloat("High-volume ROI % (not preferred)", ref highVolumeRoi, 0.5f, 8, 1_000, "%.1f%%"))
            {
                config.ProcurementHighVolumeRoiPercent = (decimal)Math.Max(8f, highVolumeRoi);
                configurationDirty = true;
            }
            ImGui.TextDisabled("The same thinner bar for high-volume, high-value stock that is not pinned as preferred.");
            var lowValueRoi = (float)config.ProcurementLowValueRoiPercent;
            if (ImGui.DragFloat("Low-value ROI %", ref lowValueRoi, 0.5f, 8, 1_000, "%.1f%%"))
            {
                config.ProcurementLowValueRoiPercent = (decimal)Math.Max(8f, lowValueRoi);
                configurationDirty = true;
            }
            ImGui.TextDisabled("Cheap stock is held to a deliberately stricter bar. A big percentage on a small stack is a side profit, not somewhere to put capital.");
            var floorRoi = (float)config.ProcurementAbsoluteMinimumRoiPercent;
            if (ImGui.DragFloat("Absolute minimum ROI %", ref floorRoi, 0.5f, 8, 1_000, "%.1f%%"))
            {
                config.ProcurementAbsoluteMinimumRoiPercent = (decimal)Math.Max(8f, floorRoi);
                configurationDirty = true;
            }
            ImGui.TextDisabled("Nothing is ever bought below this net return after fees, whatever the other bars say.");

            ImGui.Separator();
            ImGui.TextUnformatted("What counts as a high-volume market");
            var highVolumeSales = (float)config.ProcurementHighVolumeMinimumSalesPerDay;
            if (ImGui.DragFloat("High-volume minimum sales/day", ref highVolumeSales, 1f, 10, 10_000, "%.0f"))
            {
                config.ProcurementHighVolumeMinimumSalesPerDay = (decimal)Math.Max(10f, highVolumeSales);
                configurationDirty = true;
            }
            var highVolumeValue = config.ProcurementHighVolumeMinimumValuePerSlot;
            if (InputUInt("High-volume minimum stack value (gil)", ref highVolumeValue, 150_000, 100_000_000))
            {
                config.ProcurementHighVolumeMinimumValuePerSlot = highVolumeValue;
                configurationDirty = true;
            }
            ImGui.TextDisabled("Speed alone does not earn the thinner margin. A 5,000 gil item that sells quickly is not the same capital proposition as a two-million-gil stack of raid food, so a market has to clear both bars.");

            ImGui.Separator();
            ImGui.TextUnformatted("Inventory sizing (days of demand)");
            var preferredDays = (float)config.ProcurementPreferredCoverageDays;
            if (ImGui.DragFloat("Preferred coverage days", ref preferredDays, 0.1f, 0.25f, 7f, "%.1f d"))
            {
                config.ProcurementPreferredCoverageDays = (decimal)Math.Clamp(preferredDays, 0.25f, 7f);
                configurationDirty = true;
            }
            var secondaryDays = (float)config.ProcurementSecondaryCoverageDays;
            if (ImGui.DragFloat("Secondary coverage days", ref secondaryDays, 0.1f, 0.25f, 7f, "%.1f d"))
            {
                config.ProcurementSecondaryCoverageDays = (decimal)Math.Clamp(secondaryDays, 0.25f, 7f);
                configurationDirty = true;
            }
            var opportunisticDays = (float)config.ProcurementOpportunisticCoverageDays;
            if (ImGui.DragFloat("Opportunistic coverage days", ref opportunisticDays, 0.05f, 0.1f, 2f, "%.2f d"))
            {
                config.ProcurementOpportunisticCoverageDays = (decimal)Math.Clamp(opportunisticDays, 0.1f, 2f);
                configurationDirty = true;
            }
            ImGui.TextDisabled("How much stock to hold, measured in days of each market's own observed sales rather than in slots. A line selling a hundred a day can absorb several stacks and millions of gil; a line selling three a day cannot absorb one, however good the margin looks.");
            var emergencySlots = config.ProcurementEmergencyMaximumSlotsPerItem;
            if (InputInt("Emergency maximum slots per item", ref emergencySlots, 1, 60))
            {
                config.ProcurementEmergencyMaximumSlotsPerItem = emergencySlots;
                configurationDirty = true;
            }
            ImGui.TextDisabled("Limits ordinary purchase plans. Bulk preferred fast-mover bargains use the coverage target and actual bag space instead, so cheap food and potions can be bought in thousands.");
            var absorptionDays = (float)config.ProcurementAnchorAbsorptionDays;
            if (ImGui.DragFloat("Undercut absorption days", ref absorptionDays, 0.05f, 0f, 0.5f, "%.2f d"))
            {
                config.ProcurementAnchorAbsorptionDays = (decimal)Math.Clamp(absorptionDays, 0f, 0.5f);
                configurationDirty = true;
            }
            ImGui.TextDisabled("How much cheap competing stock the market swallows before it should move the expected resale price. One three-unit undercut in a market selling 150 a day is gone in minutes and should not redefine what a 99-stack is worth. Set 0 to always price against the single cheapest listing.");

            ImGui.Separator();
            ImGui.TextUnformatted("Portfolio shape");
            var preferredTarget = (float)config.PreferredPortfolioTargetPercent;
            if (ImGui.DragFloat("Preferred stock target %", ref preferredTarget, 1f, 0, 100, "%.0f%%"))
            {
                config.PreferredPortfolioTargetPercent = (decimal)Math.Clamp(preferredTarget, 0f, 100f);
                configurationDirty = true;
            }
            ImGui.TextDisabled("How much of the whole trading portfolio should be the curated food and gemdraughts, plus anything discovered that matches them. Already-listed stock counts toward this.");
            var opportunisticCap = (float)config.OpportunisticPortfolioMaximumPercent;
            if (ImGui.DragFloat("Opportunistic maximum %", ref opportunisticCap, 1f, 0, 100, "%.0f%%"))
            {
                config.OpportunisticPortfolioMaximumPercent = (decimal)Math.Clamp(opportunisticCap, 0f, 100f);
                configurationDirty = true;
            }
            ImGui.TextDisabled("The ceiling for dyes, materia and other arbitrage. Listed holdings count against it, so an existing pile blocks buying more until it sells down.");
            var slotValue = config.ProcurementMinimumProfitPerSaleSlot;
            if (InputUInt("Minimum profit per sale slot (gil)", ref slotValue, 0, 100_000_000))
            {
                config.ProcurementMinimumProfitPerSaleSlot = slotValue;
                configurationDirty = true;
            }
            ImGui.TextDisabled("A retainer slot is the scarce resource. A 300% return on a stack worth two thousand gil is not worth one. Pinned preferred stock is exempt.");

            ImGui.Separator();
            ImGui.TextUnformatted("Market discovery (optional)");
            var discovery = config.MarketDiscoveryEnabled;
            if (ImGui.Checkbox("Look for new high-value food and medicine automatically", ref discovery))
            {
                config.MarketDiscoveryEnabled = discovery;
                configurationDirty = true;
            }
            ImGui.TextDisabled("Off unless you switch it on. Uses a daily market-statistics feed to suggest additional flips, validated against the game's own item data. Suggestions only: every purchase still needs fresh live prices and all the usual checks. The six curated items stay pinned whether or not this is on, and the portfolio rules work exactly the same either way.");
            var discoveryHours = config.MarketDiscoveryCacheHours;
            if (InputInt("Refresh discovery every (hours)", ref discoveryHours, 1, 168))
            {
                config.MarketDiscoveryCacheHours = discoveryHours;
                configurationDirty = true;
            }
            var scoutAge = config.ScoutKnowledgeMaxAgeHours;
            if (InputInt("Remember away-world prices for (hours)", ref scoutAge, 1, 168))
            {
                config.ScoutKnowledgeMaxAgeHours = scoutAge;
                configurationDirty = true;
            }
            ImGui.TextDisabled("A world/item pair already seen this recently is not searched again, so trips skim only what is not already known.");
            var minimumSlots = config.ShoppingTripMinimumFreeSaleSlots;
            if (InputInt("Free sale slots before a trip", ref minimumSlots, 0, 60))
            {
                config.ShoppingTripMinimumFreeSaleSlots = minimumSlots;
                configurationDirty = true;
            }
            ImGui.TextWrapped("Wait for this many vacancies when preferred stock is covered. Continuous shopping can leave sooner to rebuild a thin food/potion position. 0 disables the vacancy hold.");
            var minimumGil = config.ShoppingTripMinimumGil;
            if (InputUInt("Gil needed before a trip", ref minimumGil, 0, 100_000_000))
            {
                config.ShoppingTripMinimumGil = minimumGil;
                configurationDirty = true;
            }
            ImGui.TextDisabled("Spendable gil after the travel reserve and any trip cap. Preferred stock is exempt from the smaller spare-stock budget.");
            var worldsPerTrip = config.PriorityWorldsPerTrip;
            if (InputInt("Worlds to scout before comparing", ref worldsPerTrip, 1, 40))
            {
                config.PriorityWorldsPerTrip = worldsPerTrip;
                configurationDirty = true;
            }
            var minutesPerTrip = config.PriorityMinutesPerTrip;
            if (InputInt("Maximum scouting minutes", ref minutesPerTrip, 5, 480))
            {
                config.PriorityMinutesPerTrip = minutesPerTrip;
                configurationDirty = true;
            }
            var scoutItems = config.PriorityItemsPerWorld;
            if (InputInt("Items to skim per away world", ref scoutItems, 1, 40))
            {
                config.PriorityItemsPerWorld = scoutItems;
                configurationDirty = true;
            }
            var homeAge = config.HomePriceMaxAgeMinutes;
            if (InputInt("Reuse home prices for (minutes)", ref homeAge, 5, 30))
            {
                config.HomePriceMaxAgeMinutes = homeAge;
                configurationDirty = true;
            }
            ImGui.TextWrapped("Reuse recent retainer price checks. The compared buying pass lasts up to 20 minutes and rechecks every purchase live. Empty or unanswered searches retry automatically.");
            var salesShare = (float)config.ProcurementWeeklySalesSharePercent;
            if (ImGui.DragFloat("Maximum stock to hold, as % of weekly sales", ref salesShare, 1f, 1, 100, "%.0f%%"))
            {
                config.ProcurementWeeklySalesSharePercent = (decimal)Math.Clamp(salesShare, 1f, 100f);
                configurationDirty = true;
            }
            ImGui.TextDisabled("Counts stock already listed on retainers and held in bags, so a cheap item is not re-bought every trip. One full stack of an item is always allowed.");
            ImGui.TextWrapped("Guided routes wait for manual buying. Start all automation uses automatic purchases.");
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
            if (ImGui.InputText("Universalis scopes (comma separated)", ref dataCenter, 128))
            {
                config.ProcurementDataCenter = dataCenter;
                configurationDirty = true;
            }
            ImGui.TextDisabled("Default: North-America. Oceania worlds are excluded from all shopping routes.");
            var liveHunt = config.LiveWorldStockHuntEnabled;
            if (ImGui.Checkbox("Use live in-game markets for automatic procurement", ref liveHunt))
            {
                config.LiveWorldStockHuntEnabled = liveHunt;
                SaveConfiguration();
            }
            var tourItems = config.LiveWorldStockHuntMaximumItems;
            if (InputInt("Items per all-world tour", ref tourItems, 1, 40))
            {
                config.LiveWorldStockHuntMaximumItems = tourItems;
                configurationDirty = true;
            }
            ImGui.TextWrapped("Full live tours walk every North America world and use your home-world price as the resale anchor. " +
                              "Only stock marked for the tour is walked, in order: raid food and potions, then General-Purpose and " +
                              "Wide-Spectrum dyes, then current materia (grades XI and XII). Each extra item is multiplied by every " +
                              "world visited, so the limit above is what keeps a tour finishing. Start all automation uses targeted " +
                              "Universalis routes instead.");
            var lowStockThreshold = config.LiveWorldStockThresholdPerItem;
            if (InputInt("Live-tour low-stock threshold per item", ref lowStockThreshold, 1, 9999))
            {
                config.LiveWorldStockThresholdPerItem = lowStockThreshold;
                configurationDirty = true;
            }
            var liveHuntCooldown = config.LiveWorldStockHuntCooldownMinutes;
            if (InputInt("Minutes between full live-world tours", ref liveHuntCooldown, 60, 10_080))
            {
                config.LiveWorldStockHuntCooldownMinutes = liveHuntCooldown;
                configurationDirty = true;
            }
            var travelCommand = config.MarketBoardTravelCommand;
            if (ImGui.InputText("Lifestream market-board command", ref travelCommand, 128))
            {
                config.MarketBoardTravelCommand = travelCommand;
                configurationDirty = true;
            }
            var bellCommand = config.SummoningBellTravelCommand;
            if (ImGui.InputText("Lifestream summoning-bell command (optional)", ref bellCommand, 128))
            {
                config.SummoningBellTravelCommand = bellCommand;
                configurationDirty = true;
            }
            ImGui.TextDisabled(
                "Leave empty to reuse the market-board command. Set a quieter destination for the return trip - " +
                "a private or free company estate, or an older-expansion city - if you would rather not park at a " +
                "busy hub. Whatever you enter is sent to chat as-is, so test it manually first.");
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
        if (log.SessionFilePath is { } sessionFile)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy log file path"))
                ImGui.SetClipboardText(sessionFile);
            ImGui.TextWrapped("This view keeps only the most recent entries. The complete run, "
                              + "including debug detail, is written to this file:");
            ImGui.TextWrapped(sessionFile);
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
        ImGui.TextDisabled("Changes are saved automatically.");
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
