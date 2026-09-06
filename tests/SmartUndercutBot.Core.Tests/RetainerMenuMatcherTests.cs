using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class RetainerMenuMatcherTests
{
    [Fact]
    public void MatchesLocalizedTextDespiteWhitespaceAndPunctuation()
    {
        var entries = new[]
        {
            "View venture report.",
            "Entrust or withdraw items.",
            "  Entrust or withdraw gil.  ",
            "Quit",
        };

        Assert.Equal(2, RetainerMenuMatcher.FindEntrustGilEntry(entries, "Entrust or withdraw gil"));
    }

    [Fact]
    public void MatchesNonEnglishLocalizedTextExactly()
    {
        var entries = new[] { "Objet verkaufen", "Gil anvertrauen/abheben", "Beenden" };

        Assert.Equal(1, RetainerMenuMatcher.FindEntrustGilEntry(entries, "Gil anvertrauen / abheben."));
    }

    [Fact]
    public void UsesNarrowEnglishFallbackForDecoratedSheetText()
    {
        var entries = new[] { "Entrust or withdraw items.", "Entrust or withdraw gil.", "Quit" };

        Assert.Equal(1, RetainerMenuMatcher.FindEntrustGilEntry(entries, "<If(Equal(...))>encoded payload"));
    }

    [Fact]
    public void DoesNotMistakeItemOrQuitRowsForGilWithdrawal()
    {
        var entries = new[] { "Entrust or withdraw items.", "View sale history.", "Quit" };

        Assert.Equal(-1, RetainerMenuMatcher.FindEntrustGilEntry(entries, string.Empty));
    }
}
