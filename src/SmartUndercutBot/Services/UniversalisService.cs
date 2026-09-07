using SmartUndercutBot.Core.Services;
using System.Net;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public interface IUniversalisService
{
    string ResolveDataCenter(string configuredDataCenter);
    IReadOnlyList<ProcurementRule> CreateFavoriteRules();
    IReadOnlyList<ProcurementRule> CreateLiquidationRules();
    IReadOnlyList<ProcurementRule> CreateBuyableDyeRules();
    IReadOnlyList<ProcurementRule> CreateTradeableMateriaRules();
    IReadOnlyList<ProcurementRule> CreateTomeMaterialRules();
    Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(
        IReadOnlyList<ProcurementRule> rules,
        string dataCenter,
        CancellationToken cancellationToken);
}

public sealed class UniversalisService : IUniversalisService, IDisposable
{
    private static readonly string[] FavoriteNames =
    [
        "Grade 4 Gemdraught of Strength",
        "Grade 4 Gemdraught of Dexterity",
        "Grade 4 Gemdraught of Intelligence",
        "Grade 4 Gemdraught of Mind",
        "Caramel Popcorn",
        "Popoto Potage",
    ];

    private const int MaximumAttempts = 3;

    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    // Universalis rate-limits aggressively; keep the plugin well under it.
    private readonly SemaphoreSlim requestSlots = new(2);
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;

    public UniversalisService(IPlayerState playerState, IDataManager dataManager)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmartUndercutter/1.0");
    }

    public string ResolveDataCenter(string configuredDataCenter)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataCenter))
            return configuredDataCenter.Trim();
        return "North-America";
    }

    public IReadOnlyList<ProcurementRule> CreateFavoriteRules()
    {
        var names = FavoriteNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return dataManager.GetExcelSheet<Item>()
            .Where(x => names.Contains(x.Name.ToString()))
            .Select(x => new ProcurementRule
            {
                ItemId = x.RowId,
                ItemName = x.Name.ToString(),
                TargetStackSize = 99,
                MaximumSaleSlots = 8,
                MinimumWeeklyUnitsSold = 20,
                AllowHighQuality = true,
                RequireHighQuality = true,
                ListFromBags = true,
                HuntOnTour = true,
                TourPriority = 0,
            })
            .OrderBy(x => x.ItemName)
            .ToArray();
    }

    // Dyes, materia and ethers are stock the player wants cleared, not traded: list
    // everything held in the bags, keep nothing back, and never plan a purchase.
    public IReadOnlyList<ProcurementRule> CreateLiquidationRules() => TradeableItems()
        .Where(x => // Never sweep up the consumables the player actually trades.
                    !ResaleStockPolicy.IsCuratedConsumable(x.Name) &&
                    (IsDye(x.Name) || IsEther(x.Name) ||
                     IsMateria(x.Row) && !ResaleStockPolicy.IsTradeableMateria(x.Name)))
        .Select(x => new ProcurementRule
        {
            ItemId = x.Row.RowId, ItemName = x.Name, TargetStackSize = 5,
            MaximumSaleSlots = 5, MinimumWeeklyUnitsSold = 0,
            ListFromBags = true, BagReserveQuantity = 0, LiquidateOnly = true,
        }).OrderBy(x => x.ItemName).ToArray();

    // The mass-market dye lines move real volume and are worth trading. Every other
    // dye stays on the sell-off list.
    public IReadOnlyList<ProcurementRule> CreateBuyableDyeRules() => TradeableItems()
        .Where(x => IsBuyableDye(x.Name))
        .Select(x => new ProcurementRule
        {
            ItemId = x.Row.RowId, ItemName = x.Name, TargetStackSize = 20,
            MaximumSaleSlots = 2, MinimumWeeklyUnitsSold = 50,
            ListFromBags = true, BagReserveQuantity = 0,
            // Dyes have no high-quality form; reading this from the sheet keeps the
            // "only buy high quality" preference from excluding them entirely.
            AllowHighQuality = x.Row.CanBeHq,
            HuntOnTour = true, TourPriority = 1,
        }).OrderBy(x => x.ItemName).ToArray();

    // Tomestone materials: list whatever is in the bags, keep none back, never buy.
    // Matching is by exact sheet name, so the caller logs how many were found - a
    // renamed or mistyped entry would otherwise seed nothing and look like a bug.
    public IReadOnlyList<ProcurementRule> CreateTomeMaterialRules() => TradeableItems()
        .Where(x => ResaleStockPolicy.IsTomeMaterial(x.Name))
        .Select(x => new ProcurementRule
        {
            ItemId = x.Row.RowId, ItemName = x.Name, TargetStackSize = 20,
            MaximumSaleSlots = 5, MinimumWeeklyUnitsSold = 0,
            ListFromBags = true, BagReserveQuantity = 0, LiquidateOnly = true,
            AllowHighQuality = x.Row.CanBeHq,
        }).OrderBy(x => x.ItemName).ToArray();

    // Grades XI and XII only. Everything older stays on the sell-off list, and this
    // sits last on the tour because current demand for it is unproven.
    public IReadOnlyList<ProcurementRule> CreateTradeableMateriaRules() => TradeableItems()
        .Where(x => IsMateria(x.Row) && ResaleStockPolicy.IsTradeableMateria(x.Name))
        .Select(x => new ProcurementRule
        {
            ItemId = x.Row.RowId, ItemName = x.Name, TargetStackSize = 20,
            MaximumSaleSlots = 1, MinimumWeeklyUnitsSold = 50,
            ListFromBags = true, BagReserveQuantity = 0,
            AllowHighQuality = x.Row.CanBeHq,
            HuntOnTour = true, TourPriority = 2,
        }).OrderBy(x => x.ItemName).ToArray();

    private IEnumerable<(Item Row, string Name)> TradeableItems() => dataManager.GetExcelSheet<Item>()
        .Where(x => !x.IsUntradable && x.ItemSearchCategory.RowId != 0)
        .Select(x => (Row: x, Name: x.Name.ToString()))
        .Where(x => !string.IsNullOrWhiteSpace(x.Name));

    private static readonly string[] BuyableDyePrefixes = ["General-Purpose ", "Wide-Spectrum "];

    private static bool IsBuyableDye(string name) =>
        name.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase) &&
        BuyableDyePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsDye(string name) =>
        name.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase) && !IsBuyableDye(name);

    // "Ether", "Hi-Ether", "Mega-Ether", "X-Ether". Matching the whole word keeps
    // "Aethersand" and anything merely containing the letters out.
    private static bool IsEther(string name) => name
        .Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
        .Any(part => string.Equals(part, "Ether", StringComparison.OrdinalIgnoreCase));

    private static bool IsMateria(Item item) => string.Equals(
        item.ItemUICategory.ValueNullable?.Name.ToString(), "Materia", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(
        IReadOnlyList<ProcurementRule> rules,
        string dataCenter,
        CancellationToken cancellationToken)
    {
        var enabled = rules.Where(x => x.Enabled && x.ItemId != 0).DistinctBy(x => x.ItemId).ToArray();
        if (enabled.Length == 0 || string.IsNullOrWhiteSpace(dataCenter))
            return [];

        var scopes = dataCenter.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var batches = scopes.SelectMany(scope => enabled.Chunk(20).Select(batch => (batch, scope))).ToArray();
        var scans = await Task.WhenAll(batches.Select(x =>
            ScanScopeAsync(x.batch, x.scope, cancellationToken))).ConfigureAwait(false);
        // A batch that never answered returns null. Partial data only ever means
        // fewer buy candidates, so keep what arrived; fail only when nothing did.
        if (scans.All(x => x is null))
            throw new HttpRequestException(
                $"Universalis did not answer any of the {batches.Length} price request(s) for this scan.");
        return scans.Where(x => x is not null).SelectMany(x => x!)
            .GroupBy(x => x.ItemId)
            .Select(group => new ProcurementMarketItem(
                group.Key,
                group.First().ItemName,
                group.SelectMany(x => x.Listings)
                    .Distinct()
                    .ToArray(),
                group.SelectMany(x => x.RecentSales)
                    .DistinctBy(x => (x.SoldAt, x.PricePerUnit, x.Quantity, x.IsHighQuality))
                    .ToArray()))
            .ToArray();
    }

    // Universalis returns 504 and 429 under load. Retry those a few times with a
    // growing delay, then give up on this batch alone. Null means "no answer", which
    // the caller treats differently from an empty market.
    private async Task<IReadOnlyList<ProcurementMarketItem>?> ScanScopeAsync(
        IReadOnlyList<ProcurementRule> enabled,
        string scope,
        CancellationToken cancellationToken)
    {
        var itemIds = string.Join(',', enabled.Select(x => x.ItemId));
        var endpoint = $"https://universalis.app/api/v2/{Uri.EscapeDataString(scope)}/{itemIds}" +
                       "?listings=100&entries=100&statsWithin=604800";
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt - 1)), cancellationToken).ConfigureAwait(false);
            await requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var response = await httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == MaximumAttempts || !IsWorthRetrying(response.StatusCode))
                        return null;
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return UniversalisResponseParser.Parse(document.RootElement,
                    enabled.ToDictionary(x => x.ItemId, x => x.ItemName));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                       ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaximumAttempts)
                    return null;
            }
            finally { requestSlots.Release(); }
        }
        return null;
    }

    private static bool IsWorthRetrying(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public void Dispose() => httpClient.Dispose();
}
