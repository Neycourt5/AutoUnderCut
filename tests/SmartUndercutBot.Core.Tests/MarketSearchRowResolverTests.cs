using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class MarketSearchRowResolverTests
{
    [Fact]
    public void RenderedListingPageMatchSurvivesAnEmptyTransientBuffer()
    {
        var row = Find(transient: [], listing: [42]);

        Assert.Equal(new MarketSearchRow(0, 42, true), row);
    }

    [Fact]
    public void ListingPageMatchIsAuthoritative()
    {
        var row = Find(rendered: 2, transient: [100, 42], listing: [42, 100], disabled: [false, false]);

        Assert.Equal(new MarketSearchRow(0, 42, true), row);
    }

    [Fact]
    public void TransientMatchIsUsedWhenTheListingPageHasNoIds()
    {
        var row = Find(rendered: 2, transient: [100, 42], listing: [0, 0], disabled: [false, false]);

        Assert.Equal(new MarketSearchRow(1, 42, true), row);
    }

    [Fact]
    public void ConflictingListingPageIdPreventsTransientFallback()
    {
        var row = Find(transient: [42], listing: [100]);

        Assert.Null(row);
    }

    [Fact]
    public void ConflictingTransientIdPreventsSingletonInference()
    {
        var row = Find(transient: [100], listing: [0]);

        Assert.Null(row);
    }

    [Fact]
    public void DisabledRowsAreNeverReturned()
    {
        Assert.Null(Find(transient: [42], disabled: [true]));
        Assert.Null(Find(listing: [42], disabled: [true]));
        Assert.Null(Find(disabled: [true]));
    }

    [Fact]
    public void EmptyBuffersAllowOneGuardedExactSingleton()
    {
        var row = Find(transient: [0], listing: [0]);

        Assert.Equal(new MarketSearchRow(0, 42, true), row);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsettledSearchRejectsExplicitMatches(bool durable)
    {
        var row = durable
            ? Find(listing: [42], settled: false)
            : Find(transient: [42], settled: false);

        Assert.Null(row);
    }

    [Theory]
    [InlineData("DYE", true, true, 1)]
    [InlineData("Dye", false, true, 1)]
    [InlineData("Dye", true, false, 1)]
    [InlineData("Dye", true, true, 2)]
    public void SingletonInferenceRequiresExactTextModeSettledAndOneRow(
        string currentText,
        bool normalMode,
        bool settled,
        int rendered)
    {
        var row = Find(currentText: currentText, normalMode: normalMode, settled: settled, rendered: rendered,
            transient: [], listing: [], disabled: Enumerable.Repeat(false, rendered).ToArray());

        Assert.Null(row);
    }

    [Fact]
    public void SearchIsCappedAtOneHundredRenderedRows()
    {
        var ids = Enumerable.Repeat(100u, 101).ToArray();
        ids[99] = 42;
        var row = Find(rendered: 101, transient: ids, listing: [], disabled: new bool[101]);
        Assert.Equal(99, row?.Index);

        ids[99] = 100;
        ids[100] = 42;
        Assert.Null(Find(rendered: 101, transient: ids, listing: [], disabled: new bool[101]));
    }

    [Fact]
    public void ShortSourcesAndInvalidRequestsFailClosed()
    {
        Assert.Null(Find(rendered: 4, transient: [100], listing: [], disabled: []));
        Assert.Null(Find(expectedId: 0));
        Assert.Null(Find(expectedName: string.Empty));
        Assert.Null(Find(rendered: 0));
        Assert.Null(Find(rendered: -1));
    }

    private static MarketSearchRow? Find(
        uint expectedId = 42,
        string expectedName = "Dye",
        string currentText = "Dye",
        int rendered = 1,
        IReadOnlyList<uint>? transient = null,
        IReadOnlyList<uint>? listing = null,
        IReadOnlyList<bool>? disabled = null,
        bool settled = true,
        bool normalMode = true) =>
        MarketSearchRowResolver.FindExact(
            expectedId,
            expectedName,
            currentText,
            rendered,
            transient ?? [],
            listing ?? [],
            disabled ?? [false],
            settled,
            normalMode);
}
