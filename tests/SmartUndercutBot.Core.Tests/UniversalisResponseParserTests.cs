using System.Text.Json;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class UniversalisResponseParserTests
{
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
