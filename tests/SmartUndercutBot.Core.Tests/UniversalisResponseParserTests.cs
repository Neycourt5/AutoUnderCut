using System.Text.Json;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class UniversalisResponseParserTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DailyVelocityPreservesQualityAndFractionalUnits(bool multipleItems)
    {
        const string item = """{"itemID":1,"regularSaleVelocity":99999,"nqSaleVelocity":12.75,"hqSaleVelocity":"4.25"}""";
        using var document = JsonDocument.Parse(multipleItems ? "{\"items\":{\"1\":" + item + "}}" : item);
        var market = Assert.Single(UniversalisResponseParser.Parse(document.RootElement,
            new Dictionary<uint, string> { [1] = "Food" }));
        Assert.Equal(12.75m, market.NqSalesPerDay);
        Assert.Equal(4.25m, market.HqSalesPerDay);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("\"NaN\"")]
    [InlineData("1e99")]
    [InlineData("true")]
    public void InvalidVelocityIsMissingWhileZeroRemainsKnown(string invalid)
    {
        using var document = JsonDocument.Parse("{\"itemID\":1,\"nqSaleVelocity\":0,\"hqSaleVelocity\":" + invalid + "}");
        var market = Assert.Single(UniversalisResponseParser.Parse(document.RootElement,
            new Dictionary<uint, string> { [1] = "Food" }));
        Assert.Equal(0m, market.NqSalesPerDay);
        Assert.Null(market.HqSalesPerDay);
    }

    [Fact]
    public void CombinedVelocityDoesNotStandInForAnAbsentQualityRate()
    {
        using var document = JsonDocument.Parse("""{"itemID":1,"regularSaleVelocity":500}""");
        var market = Assert.Single(UniversalisResponseParser.Parse(document.RootElement,
            new Dictionary<uint, string> { [1] = "Food" }));
        Assert.Null(market.NqSalesPerDay);
        Assert.Null(market.HqSalesPerDay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldScopedResponsesSupplyWorldForListings(bool multipleItems)
    {
        const string item = """
            {"itemID":1,"listings":[{"pricePerUnit":1000,"quantity":99,"hq":true}]}
            """;
        var json = multipleItems
            ? "{\"worldName\":\"Siren\",\"worldID\":57,\"items\":{\"1\":" + item + "}}"
            : item[..^1] + ",\"worldName\":\"Siren\",\"worldID\":57}";
        using var document = JsonDocument.Parse(json);
        var market = Assert.Single(UniversalisResponseParser.Parse(document.RootElement, new Dictionary<uint, string> { [1] = "Popcorn" }));
        var listing = Assert.Single(market.Listings);
        Assert.Equal("Siren", listing.WorldName);
        Assert.Equal(57u, listing.WorldId);
    }

    [Fact]
    public void HashedListingIdsDoNotCollapseDistinctStacks()
    {
        using var document = JsonDocument.Parse("""
            {"itemID":1,"worldName":"Siren","listings":[
              {"listingID":"abcdef123456","retainerID":"fedcba987654","pricePerUnit":1000,"quantity":99,"hq":true},
              {"listingID":"abcdef999999","retainerID":"fedcba987654","pricePerUnit":1000,"quantity":99,"hq":true}
            ]}
            """);
        var market = Assert.Single(UniversalisResponseParser.Parse(document.RootElement, new Dictionary<uint, string> { [1] = "Popcorn" }));
        Assert.Equal(2, market.Listings.Distinct().Count());
        Assert.All(market.Listings, listing => Assert.Equal(0ul, listing.ListingId));
    }
}
