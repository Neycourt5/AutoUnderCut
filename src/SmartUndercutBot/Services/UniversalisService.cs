using SmartUndercutBot.Core.Services;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public interface IUniversalisService
{
    string ResolveDataCenter(string configuredDataCenter);
    IReadOnlyList<ProcurementRule> CreateFavoriteRules();
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
    ];

    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
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
        return "North-America,Oceania";
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
            })
            .OrderBy(x => x.ItemName)
            .ToArray();
    }

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
        var scans = await Task.WhenAll(scopes.Select(scope =>
            ScanScopeAsync(enabled, scope, cancellationToken))).ConfigureAwait(false);
        return scans.SelectMany(x => x)
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

    private async Task<IReadOnlyList<ProcurementMarketItem>> ScanScopeAsync(
        IReadOnlyList<ProcurementRule> enabled,
        string scope,
        CancellationToken cancellationToken)
    {
        var itemIds = string.Join(',', enabled.Select(x => x.ItemId));
        var endpoint = $"https://universalis.app/api/v2/{Uri.EscapeDataString(scope)}/{itemIds}" +
                       "?listings=100&entries=100&statsWithin=604800";
        using var response = await httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return UniversalisResponseParser.Parse(document.RootElement,
            enabled.ToDictionary(x => x.ItemId, x => x.ItemName));
    }

    public void Dispose() => httpClient.Dispose();
}
