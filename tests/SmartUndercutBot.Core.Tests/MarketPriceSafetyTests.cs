using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class MarketPriceSafetyTests
{
    [Theory]
    [InlineData(999_999_999u, 99u)]
    [InlineData(10_101_010u, 99u)]
    public void RecognizesDirectAndStackTotalPlaceholderPrices(uint price, uint quantity)
    {
        Assert.True(MarketPriceSafety.IsSafetySeedRepresentation(price, quantity));
        Assert.False(MarketPriceSafety.IsSafeCuratedUnitPrice(price, quantity));
    }

    [Theory]
    [InlineData(3_300u, 99u)]
    [InlineData(6_371u, 99u)]
    [InlineData(5_424u, 99u)]
    public void AcceptsNormalCuratedConsumablePrices(uint price, uint quantity)
    {
        Assert.False(MarketPriceSafety.IsSafetySeedRepresentation(price, quantity));
        Assert.True(MarketPriceSafety.IsSafeCuratedUnitPrice(price, quantity));
    }

    [Fact]
    public void RejectsOtherMillionGilCuratedPrices()
    {
        Assert.False(MarketPriceSafety.IsSafeCuratedUnitPrice(1_000_001, 99));
    }
}
