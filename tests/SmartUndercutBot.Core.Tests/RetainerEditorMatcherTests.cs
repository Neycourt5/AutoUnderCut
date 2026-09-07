using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class RetainerEditorMatcherTests
{
    private static readonly RetainerListing SkyBlue = new(1, "Retainer", 7, 13721,
        "General-purpose Metallic Sky Blue Dye", 5, 6598, false);

    [Theory]
    [InlineData("General-purpose Metallic Sky Blue\nDye")]
    [InlineData(" General-purpose Metallic Sky Blue\r\nDye ")]
    [InlineData("General-purpose Metallic\u00a0Sky Blue Dye\u200b")]
    public void WrappedDyeNameResolvesToTheCorrectInventorySlot(string name)
    {
        RetainerListing[] slots = [SkyBlue with { ItemId = 13722, ItemName = "General-purpose Metallic Blue Dye", Slot = 6 }, SkyBlue];
        Assert.Equal(SkyBlue, RetainerEditorMatcher.Resolve(slots, new HashSet<short>(), 13721, name, 5, 6598, false));
    }

    [Fact]
    public void SimilarNameWrongQuantityQualityAndClaimedSlotsCannotMatch()
    {
        Assert.Null(RetainerEditorMatcher.Resolve([SkyBlue], new HashSet<short>(), 0, "General-purpose Metallic Blue Dye", 5, 6598, false));
        Assert.Null(RetainerEditorMatcher.Resolve([SkyBlue], new HashSet<short>(), 0, SkyBlue.ItemName, 2, 6598, false));
        Assert.Null(RetainerEditorMatcher.Resolve([SkyBlue], new HashSet<short>(), 0, SkyBlue.ItemName, 5, 6598, true));
        Assert.Null(RetainerEditorMatcher.Resolve([SkyBlue], new HashSet<short> { 7 }, 13721, SkyBlue.ItemName, 5, 6598, false));
    }
}
