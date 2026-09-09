using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

public sealed unsafe class MarketPurchaseService : IMarketPurchaseService, IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly AutomationLog log;
    private readonly MarketSearchSession search;
    private readonly IMarketBoard marketBoard;
    private readonly MarketResponseTracker response = new();
    private readonly Hook<RequestResultDelegate> requestResultHook;
    private readonly Hook<PurchaseResponseDelegate> purchaseResponseHook;
    private delegate void RequestResultDelegate(InfoProxyItemSearch* proxy, byte count, int error);
    private delegate void PurchaseResponseDelegate(InfoProxyItemSearch* proxy, uint itemId, uint error);
    private uint pendingPurchaseItem;
    private uint purchaseError;
    public uint PurchaseError => purchaseError;
    private string lastSearchStatus = string.Empty;
    public string? SearchStatus => $"{search.Status} ({response.Summary})";

    public MarketPurchaseService(
        IObjectTable objectTable,
        IGameGui gameGui,
        IDataManager dataManager,
        AutomationLog log, IMarketBoard marketBoard, IGameInteropProvider interop)
    {
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.log = log;
        this.marketBoard = marketBoard;
        search = new MarketSearchSession(new SearchUi(this));
        requestResultHook = interop.HookFromAddress<RequestResultDelegate>(
            (nint)InfoProxyItemSearch.MemberFunctionPointers.ProcessRequestResult, OnRequestResult);
        purchaseResponseHook = interop.HookFromAddress<PurchaseResponseDelegate>(
            (nint)InfoProxyItemSearch.MemberFunctionPointers.ProcessPurchaseResponse, OnPurchaseResponse);
        requestResultHook.Enable();
        purchaseResponseHook.Enable();
        marketBoard.OfferingsReceived += OnOfferings;
    }

    private void OnRequestResult(InfoProxyItemSearch* proxy, byte count, int error)
    {
        var id = proxy == null ? 0 : proxy->SearchItemId;
        requestResultHook.Original(proxy, count, error);
        response.ReceiveCount(id, count, error);
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings) => response.ReceiveRows(
        offerings.RequestId, offerings.ItemListings.FirstOrDefault()?.ItemId ?? 0,
        offerings.ItemListings.Select(x => x.ListingId));

    private void OnPurchaseResponse(InfoProxyItemSearch* proxy, uint itemId, uint error)
    {
        purchaseResponseHook.Original(proxy, itemId, error);
        if (pendingPurchaseItem == 0 || itemId != pendingPurchaseItem) return;
        purchaseError = error;
        log.Add(error == 0 ? AutomationLogLevel.Information : AutomationLogLevel.Warning,
            $"MARKET BUY server response for item {itemId}: error {error}. Inventory verification still required.");
    }

    public bool IsMarketBoardOpen => IsAddonVisible("ItemSearch");
    public uint FreeInventorySlots
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetEmptySlotsInBag();
        }
    }
    public uint Gil
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetGil();
        }
    }

    public int GetInventoryCount(uint itemId, bool highQuality)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(itemId, highQuality, false, false, 0);
    }

    public Vector3? FindNearest(string objectName) => objectTable
        .Where(x => string.Equals(x.Name.TextValue, objectName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => Vector3.DistanceSquared(x.Position, objectTable.LocalPlayer?.Position ?? Vector3.Zero))
        .Select(x => (Vector3?)x.Position)
        .FirstOrDefault();

    public float DistanceTo(Vector3 position) => objectTable.LocalPlayer is { } player
        ? Vector3.Distance(player.Position, position)
        : float.MaxValue;

    public bool InteractNearest(string objectName, float maximumDistance = 5f)
    {
        var player = objectTable.LocalPlayer;
        var target = objectTable
            .Where(x => string.Equals(x.Name.TextValue, objectName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => player is null ? float.MaxValue : Vector3.DistanceSquared(x.Position, player.Position))
            .FirstOrDefault();
        if (target is null || player is null || Vector3.Distance(target.Position, player.Position) > maximumDistance)
            return false;
        var targetSystem = TargetSystem.Instance();
        return targetSystem != null && targetSystem->InteractWithObject((GameObject*)target.Address, false) != 0;
    }

    public bool RequestListings(uint itemId)
    {
        if (itemId == 0 || !dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return false;
        var started = search.Request(itemId, item.Name.ToString());
        ReportSearchStatus();
        return started;
    }

    public bool AreListingsReady(uint itemId)
    {
        var ready = search.Poll(itemId);
        ReportSearchStatus();
        return ready;
    }

    public void ResetListingRequest()
    {
        search.Reset();
        lastSearchStatus = string.Empty;
    }

    private void ReportSearchStatus()
    {
        if (lastSearchStatus == search.Status) return;
        lastSearchStatus = search.Status;
        log.Add(AutomationLogLevel.Information, $"MARKET SEARCH {search.Status}");
    }

    private sealed class SearchUi(MarketPurchaseService owner) : IMarketSearchUi
    {
        public string? LastBlocker { get; private set; }

        public bool PrepareSearch(string name)
        {
            var addon = owner.gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (!CanUseInput(addon)) return false;
            addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, 0);
            if (FocusSearchInput(addon))
            {
                SetSearchInput(addon, string.Empty);
                SetSearchInput(addon, name);
                return true;
            }
            LastBlocker = "the search box has no focusable node";
            return false;
        }

        public bool SubmitSearch(string name)
        {
            var addon = owner.gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (!CanUseInput(addon)) return false;

            // End a stale listing request rather than waiting on it. Gating the name
            // search on WaitingForListings deadlocks: that flag describes an in-flight
            // request for one item's listings, nothing clears it on its own, and the
            // search box then sits focused and empty for the rest of the route.
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy != null && proxy->WaitingForListings)
            {
                proxy->EndRequest();
                owner.log.Add(AutomationLogLevel.Information,
                    "MARKET SEARCH ended a stale listing request before searching again.");
            }
            if (!FocusSearchInput(addon))
            {
                LastBlocker = "the search box has no focusable node";
                return false;
            }

            // Drive the input's own callbacks, which update the game's search state.
            // Mirroring text into the addon's cached strings can leave a typed query
            // with no submitted search.
            // PrepareSearch typed the query on an earlier tick. Let TextChanged
            // reach the game before pressing Enter, as with a manual search.
            var input = &addon->SearchTextInput->AtkComponentInputBase;
            var callback = "RunSearch fallback";
            if (input->Callback != null)
                callback = input->Callback(&addon->AtkUnitBase, InputCallbackType.Enter,
                    input->RawString.StringPtr, input->EvaluatedString.StringPtr,
                    input->CallbackEventKind).ToString();
            else
                // No input callback to drive: use the addon's own entry point rather
                // than stalling the whole route.
                addon->RunSearch(true);
            owner.log.Add(AutomationLogLevel.Information,
                $"MARKET SEARCH submitted '{name}'; mode={addon->Mode}, " +
                $"filter={addon->SelectedFilter}, callback={callback}.");
            return true;
        }

        private bool CanUseInput(AddonItemSearch* addon)
        {
            LastBlocker = addon == null ? "the Item Search window is not loaded"
                : !addon->IsVisible ? "the Item Search window is not visible"
                : !addon->IsReady ? "the Item Search window is still initialising"
                : addon->SearchTextInput == null ? "the search box is missing"
                : addon->ResultsList == null ? "the results list is missing"
                : null;
            return LastBlocker is null;
        }

        private static bool FocusSearchInput(AddonItemSearch* addon)
        {
            var input = &addon->SearchTextInput->AtkComponentInputBase;
            var component = &input->AtkComponentBase;
            var node = input->CollisionNode != null ? &input->CollisionNode->AtkResNode
                : component->OwnerNode != null ? &component->OwnerNode->AtkResNode : null;
            if (node == null) return false;
            addon->AtkUnitBase.Focus();
            addon->AtkUnitBase.SetFocusNode(node, true);
            addon->AtkUnitBase.SetComponentFocusNode(component);
            var stage = AtkStage.Instance();
            if (stage != null && stage->AtkInputManager != null)
                stage->AtkInputManager->SetFocus(node, &addon->AtkUnitBase, 0);
            input->IsActive = true;
            return true;
        }

        private static void SetSearchInput(AddonItemSearch* addon, string text)
        {
            var widget = addon->SearchTextInput;
            widget->SetText(text);
            var input = &widget->AtkComponentInputBase;
            input->CursorPos = input->SelectionStart = input->SelectionEnd = text.Length;
            if (input->Callback != null)
                input->Callback(&addon->AtkUnitBase, InputCallbackType.TextChanged,
                    input->RawString.StringPtr, input->EvaluatedString.StringPtr, input->CallbackEventKind);
        }

        // Reports which of ReadRows' preconditions is actually refusing, and the
        // counts when none of them is. A stalled search then names its own cause in
        // the session log instead of only saying that nothing is visible.
        public string? RowDiagnostics
        {
            get
            {
                var addon = owner.gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
                if (addon == null) return "the Item Search window is not loaded";
                if (!addon->IsVisible) return "the Item Search window is not visible";
                if (!addon->IsReady) return "the Item Search window is still initialising";
                if (addon->ResultsList == null) return "the results list is missing";
                var agent = AgentItemSearch.Instance();
                if (agent == null) return "the item search agent is unavailable";
                if (agent->ItemBuffer == null) return "the item search agent has no result buffer";
                if (agent->IsPartialSearching) return "the game is still running a partial search";
                if (agent->IsItemPushPending) return "the game is still pushing result rows";
                var proxy = InfoProxyItemSearch.Instance();
                return $"agent rows {agent->ItemCount}, list rows {addon->ResultsList->GetItemCount()}, " +
                       $"mode {addon->Mode}, filter {addon->SelectedFilter}, " +
                       $"proxy item {(proxy == null ? 0 : proxy->SearchItemId)}, " +
                       $"awaiting listings {(proxy != null && proxy->WaitingForListings)}";
            }
        }

        public IReadOnlyList<MarketSearchRow> ReadRows()
        {
            var addon = owner.gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            var agent = AgentItemSearch.Instance();
            if (addon == null || !addon->IsVisible || addon->ResultsList == null ||
                agent == null || agent->ItemBuffer == null || agent->IsPartialSearching || agent->IsItemPushPending)
                return [];
            // Name searches populate ItemBuffer/ItemCount. ListingPageItemIds
            // belongs to category pages and can be empty or left over from browsing.
            var ids = agent->ItemBuffer;
            var count = Math.Min((int)Math.Min(agent->ItemCount, 100u), addon->ResultsList->GetItemCount());
            var rows = new List<MarketSearchRow>();
            for (var i = 0; i < count; i++)
                rows.Add(new(i, ids[i], !addon->ResultsList->GetItemDisabledState(i)));
            return rows;
        }

        public bool ActivateRow(int index, uint itemId)
        {
            // Re-read the exact identity immediately before crossing the native boundary.
            if (!ReadRows().Any(x => x.Index == index && x.ItemId == itemId && x.Enabled)) return false;
            var addon = owner.gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (addon == null || addon->ResultsList == null ||
                (!addon->ResultsList->IsItemInteractionEnabled && !addon->ResultsList->IsItemClickEnabled)) return false;
            owner.response.Begin(itemId);
            addon->ResultsList->SelectItem(index, true);
            addon->ResultsList->DispatchItemEvent(index, AtkEventType.ListItemClick);
            return true;
        }

        public MarketSearchResult ReadResult()
        {
            var proxy = InfoProxyItemSearch.Instance();
            return new(owner.IsAddonVisible("ItemSearchResult"), proxy == null ? 0 : proxy->SearchItemId,
                proxy == null || proxy->WaitingForListings,
                proxy != null && owner.response.IsReady(proxy->SearchItemId, (int)proxy->ListingCount));
        }

        public void CloseResult() => owner.CloseAddon("ItemSearchResult");

        public void ClearSearch()
        {
            owner.response.Begin(0);
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy != null)
            {
                if (proxy->WaitingForListings) proxy->EndRequest();
                proxy->ClearListData();
            }
            CloseResult();
        }
    }

    public IReadOnlyList<LivePurchaseListing> ReadLiveListings(uint itemId)
    {
        var proxy = InfoProxyItemSearch.Instance();
        // WaitingForListings stays set on this client after a complete response, so
        // gating on it returned an empty board with every row already in memory.
        // The tracker is the real signal: declared rows all received, no error.
        if (proxy == null || proxy->SearchItemId != itemId ||
            !response.IsReady(itemId, (int)proxy->ListingCount))
            return [];

        var result = new List<LivePurchaseListing>();
        var source = proxy->Listings;
        var count = (int)Math.Min(proxy->ListingCount, (uint)source.Length);
        for (var index = 0; index < count; index++)
        {
            var row = source[index];
            if (row.ItemId == itemId && row.UnitPrice > 0 && row.Quantity > 0 && !row.IsMannequin &&
                response.Contains(row.ListingId))
                result.Add(new(index, row.ItemId, row.ListingId, row.RetainerId,
                    row.UnitPrice, row.Quantity, row.IsHqItem, row.TotalTax));
        }
        return result;
    }

    public bool TrySelectLiveListing(
        ProcurementOrder expected,
        IReadOnlySet<ulong> excludedRetainerIds,
        out LivePurchaseListing? listing)
    {
        listing = null;
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId != expected.ItemId ||
            !response.IsReady(expected.ItemId, (int)proxy->ListingCount))
            return false;

        var candidates = new List<LivePurchaseListing>();
        var source = proxy->Listings;
        var count = (int)Math.Min(proxy->ListingCount, (uint)source.Length);
        for (var index = 0; index < count; index++)
        {
            var row = source[index];
            if (row.ItemId != expected.ItemId || row.UnitPrice == 0 || row.UnitPrice > expected.MaximumAcceptableUnitPrice ||
                row.Quantity == 0 || row.Quantity > expected.Quantity || row.IsHqItem != expected.IsHighQuality ||
                excludedRetainerIds.Contains(row.RetainerId) || row.IsMannequin || !response.Contains(row.ListingId))
                continue;
            candidates.Add(new(index, row.ItemId, row.ListingId, row.RetainerId,
                row.UnitPrice, row.Quantity, row.IsHqItem, row.TotalTax));
        }

        listing = candidates.FirstOrDefault(x => x.ListingId == expected.ListingId && x.RetainerId == expected.RetainerId)
                  ?? candidates.OrderBy(x => x.PricePerUnit).ThenBy(x => x.Quantity).FirstOrDefault();
        return listing is not null;
    }

    public bool SubmitPurchase(LivePurchaseListing listing)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId != listing.ItemId ||
            !response.Contains(listing.ListingId) || !response.IsReady(listing.ItemId, (int)proxy->ListingCount) ||
            !IsAddonVisible("ItemSearchResult") || listing.Index < 0 ||
            listing.Index >= proxy->ListingCount || listing.Index >= proxy->Listings.Length)
            return false;
        var source = proxy->Listings;
        fixed (MarketBoardListing* rows = source)
        {
            var row = &rows[listing.Index];
            if (row->IsMannequin || row->ListingId != listing.ListingId || row->RetainerId != listing.RetainerId ||
                row->ItemId != listing.ItemId || row->UnitPrice != listing.PricePerUnit ||
                row->Quantity != listing.Quantity || row->IsHqItem != listing.IsHighQuality ||
                row->TotalTax != listing.TotalTax)
                return false;
            if (!proxy->SetLastPurchasedItem(row))
            {
                log.Add(AutomationLogLevel.Warning, $"MARKET BUY could not prepare listing {listing.ListingId} for purchase.");
                return false;
            }
            pendingPurchaseItem = listing.ItemId;
            purchaseError = 0;
            var sent = proxy->SendPurchaseRequestPacket();
            log.Add(sent ? AutomationLogLevel.Information : AutomationLogLevel.Warning,
                $"MARKET BUY request {(sent ? "sent" : "rejected locally")} for item {listing.ItemId}, " +
                $"listing {listing.ListingId}, quantity {listing.Quantity}, unit price {listing.PricePerUnit:N0}. " +
                (sent ? "Waiting for inventory confirmation." : "No purchase confirmation received."));
            return sent;
        }
    }

    // This adapter sends the game's purchase packet directly. It never opens a
    // confirmation dialog, so accepting an unrelated SelectYesno is unnecessary.

    public void CloseMarketBoard()
    {
        ResetListingRequest();
        CloseAddon("ItemSearchResult");
        CloseAddon("ItemSearch");
    }

    public void CloseRetainerList() => CloseAddon("RetainerList");

    private bool IsAddonVisible(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    private void CloseAddon(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }

    public void Dispose()
    {
        marketBoard.OfferingsReceived -= OnOfferings;
        requestResultHook.Dispose();
        purchaseResponseHook.Dispose();
    }

}
