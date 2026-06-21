using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using SmartUndercutBot.Automation;

namespace SmartUndercutBot.Windows;

public sealed class GuidedProcurementWindow : Window
{
    private readonly ProcurementController procurement;

    public GuidedProcurementWindow(ProcurementController procurement)
        : base("Guided Market Route##SmartUndercutterGuided")
    {
        this.procurement = procurement;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(650, 300),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public override bool DrawConditions() => procurement.IsGuidedReviewPending;

    public override void Draw()
    {
        var orders = procurement.CurrentGuidedWorldOrders;
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f),
            $"World {procurement.CurrentGuidedWorldNumber} / {procurement.GuidedWorldCount}: {procurement.CurrentGuidedWorld}");
        ImGui.TextWrapped("Review the live market board and buy only listings you still consider worthwhile. Universalis prices may be stale; the ceiling is the highest guarded unit price from the scan.");
        ImGui.Separator();

        if (ImGui.BeginTable("GuidedDeals", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 170 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Expected qty");
            ImGui.TableSetupColumn("Expected/unit");
            ImGui.TableSetupColumn("Do not exceed");
            ImGui.TableSetupColumn("Target resale");
            ImGui.TableSetupColumn("Expected profit");
            ImGui.TableHeadersRow();
            foreach (var order in orders)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{order.ItemName}{(order.IsHighQuality ? " HQ" : string.Empty)}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(order.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{order.PricePerUnit:N0}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{order.MaximumAcceptableUnitPrice:N0}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{order.TargetSalePrice:N0}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{order.ExpectedProfit:N0}");
            }
            ImGui.EndTable();
        }

        var lastWorld = procurement.CurrentGuidedWorldNumber >= procurement.GuidedWorldCount;
        if (ImGui.Button(lastWorld ? "Done here — return home" : "Done here — next world"))
            procurement.CompleteGuidedWorldReview();
        ImGui.SameLine();
        if (ImGui.Button("Stop guided route"))
            procurement.Halt("Guided market route stopped by user.");
    }
}
