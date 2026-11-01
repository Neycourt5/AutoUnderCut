using System.Text.Json;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>Fixtures follow the shapes of live answers recorded on 2026-11-01.</summary>
public sealed class MarketScanParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793508000);

    private const string RegionView = """
    {"items":{"49240":{"itemID":49240,
      "worldUploadTimes":{"404":1793500359039,"64":1793507000000},
      "listings":[
        {"lastReviewTime":1793500343,"pricePerUnit":1450,"quantity":99,"worldName":"Marilith","worldID":404,"hq":true,"listingID":"28428972841177155","retainerID":"33777097242152083","retainerName":"Stopretailfraud"},
        {"lastReviewTime":1793506900,"pricePerUnit":1500,"quantity":50,"worldName":"Leviathan","worldID":64,"hq":true,"listingID":"1","retainerID":"2","retainerName":"A"},
        {"lastReviewTime":1793506900,"pricePerUnit":1510,"quantity":99,"worldName":"Leviathan","worldID":64,"hq":true,"listingID":"3","retainerID":"4","retainerName":"B"},
        {"lastReviewTime":1793506900,"pricePerUnit":400,"quantity":99,"worldName":"Leviathan","worldID":64,"hq":false,"listingID":"5","retainerID":"6","retainerName":"C"},
        {"lastReviewTime":1793507900,"pricePerUnit":1600,"quantity":99,"worldName":"Siren","worldID":57,"hq":true,"listingID":"7","retainerID":"8","retainerName":"D"}
      ]}}}
    """;

    [Fact]
    public void RegionViewBecomesOneDatedSnapshotPerAwayWorld()
    {
        using var document = JsonDocument.Parse(RegionView);
        var snapshots = UniversalisMemoryParser.ParseWorldSnapshots(document.RootElement, true, "Siren", 100, Now);

        Assert.Equal(["Leviathan", "Marilith"], snapshots.Select(x => x.World).Order());
        var leviathan = snapshots.Single(x => x.World == "Leviathan");
        Assert.Equal(2, leviathan.Listings);
        Assert.Equal(149UL, leviathan.Units);
        Assert.Equal(1_500u, leviathan.Lowest);
        Assert.Equal(PriceSource.Universalis, leviathan.Source);
        // Dated by the world's own upload, not by when we asked.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793507000000), leviathan.ObservedAt);
        // Fewer rows than requested: the view is complete.
        Assert.Equal(0u, leviathan.CompleteBelowPrice);
    }

    [Fact]
    public void AFullPageIsOnlyCompleteBelowItsDearestRow()
    {
        using var document = JsonDocument.Parse(RegionView);
        var snapshots = UniversalisMemoryParser.ParseWorldSnapshots(document.RootElement, true, "Siren", 5, Now);
        Assert.All(snapshots, x => Assert.Equal(1_600u, x.CompleteBelowPrice));
    }

    [Fact]
    public void HomeViewExcludesOurRetainersAndMeasuresTheLastWeek()
    {
        const string home = """
        {"itemID":49240,"worldName":"Siren","lastUploadTime":1793507900000,"hqSaleVelocity":1663.7,
         "listings":[
           {"pricePerUnit":2600,"quantity":99,"hq":true,"retainerName":"Mofumofumofumofu"},
           {"pricePerUnit":2895,"quantity":1,"hq":true,"retainerName":"Veilia"},
           {"pricePerUnit":2900,"quantity":99,"hq":true,"retainerName":"Other"},
           {"pricePerUnit":900,"quantity":99,"hq":false,"retainerName":"Cheap"}],
         "recentHistory":[
           {"hq":true,"pricePerUnit":2900,"quantity":71,"timestamp":1793505970},
           {"hq":true,"pricePerUnit":2800,"quantity":50,"timestamp":1793480000},
           {"hq":true,"pricePerUnit":2700,"quantity":30,"timestamp":1793422000},
           {"hq":true,"pricePerUnit":1,"quantity":1,"timestamp":1792000000},
           {"hq":false,"pricePerUnit":500,"quantity":99,"timestamp":1793505000}]}
        """;
        using var document = JsonDocument.Parse(home);
        var snapshot = Assert.Single(UniversalisMemoryParser.ParseHomeSnapshots(document.RootElement, true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mofumofumofumofu" }, Now));

        Assert.Equal(2_895u, snapshot.LowestCompetitor);
        Assert.Equal(2, snapshot.CompetingListings);
        Assert.Equal(100UL, snapshot.CompetingUnits);
        Assert.Equal(1663.7m, snapshot.UnitsPerDay);
        // Three HQ sales inside the week; the week-old outlier is outside it.
        Assert.Equal(2_800u, snapshot.MedianSalePrice);
        Assert.Equal(50u, snapshot.MedianSaleQuantity);
        Assert.True(snapshot.SalesPerDay > 2m);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793507900000), snapshot.ObservedAt);
    }

    [Fact]
    public void MarketshareRowsAcceptStringNumbersAndBecomeDailyRates()
    {
        const string body = """
        {"data":[
          {"avg":2672,"itemID":"49240","marketValue":39103799,"median":2694,"minPrice":2895,"name":"Caramel Popcorn","npc_vendor_info":"","purchaseAmount":248,"quantitySold":14567,"url":"https://universalis.app/market/49240","percentChange":8.35,"state":"stable"},
          {"avg":5000,"itemID":"44178","marketValue":100,"median":4800,"minPrice":0,"name":"Gone","purchaseAmount":14,"quantitySold":20,"percentChange":760,"state":"out of stock"},
          {"itemID":"0","name":"broken"}
        ]}
        """;
        using var document = JsonDocument.Parse(body);
        var rows = SaddlebagParser.ParseMarketshare(document.RootElement);
        Assert.Equal(2, rows.Count);

        var popcorn = SaddlebagParser.ToCandidate(rows[0], 168m, true, "Meals", Now);
        Assert.Equal(49240u, popcorn.ItemId);
        Assert.Equal(14_567m / 7m, popcorn.UnitsPerDay);
        Assert.Equal(248m / 7m, popcorn.SalesPerDay);
        // The lower of the median and the current floor.
        Assert.Equal(2_694u, popcorn.HomePrice);
        Assert.Equal("stable", popcorn.Trend);
        Assert.Equal(PriceSource.Saddlebag, popcorn.Source);

        // Out of stock: the sentinel zero floor does not become the price.
        Assert.Equal(4_800u, SaddlebagParser.ToCandidate(rows[1], 168m, false, "Meals", Now).HomePrice);
    }

    [Fact]
    public void AnEmptySaddlebagAnswerIsAnEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.Empty(SaddlebagParser.ParseMarketshare(document.RootElement));
    }

    [Fact]
    public void CandidateMarginIsNetOfBothTaxes()
    {
        var candidate = new MarketCandidate(1, "x", true, "Meals", 100m, 10m, 2_000, 1_000, "Exodus", "stable",
            PriceSource.Saddlebag, Now);
        // 2,000 x 0.95 = 1,900 against 1,000 x 1.05 = 1,050.
        Assert.Equal(80.95m, decimal.Round(candidate.NetRoiPercent(5m, 5m)!.Value, 2));
        Assert.Equal(10m, candidate.UnitsPerSale);
        Assert.Null((candidate with { CheapestElsewhere = 0 }).NetRoiPercent(5m, 5m));
    }
}
