using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class WealthHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static WealthSample Sample(int minutes, ulong total) =>
        new(Start.AddMinutes(minutes), total / 2, total / 2, total);

    [Fact]
    public void RapidScansUpdateTheLatestPointInsteadOfFloodingTheSeries()
    {
        var history = new WealthHistory();
        var interval = TimeSpan.FromMinutes(15);
        Assert.True(history.Record(Sample(0, 1_000), interval));
        Assert.True(history.Record(Sample(5, 2_000), interval));
        Assert.True(history.Record(Sample(10, 3_000), interval));

        var only = Assert.Single(history.Samples);
        Assert.Equal(3_000ul, only.Total);

        // Past the interval, measured from the point it replaced, it becomes a new one.
        Assert.True(history.Record(Sample(30, 4_000), interval));
        Assert.Equal(2, history.Samples.Count);
    }

    [Fact]
    public void AnOutOfOrderSampleIsRefusedRatherThanDrawingBackwards()
    {
        var history = new WealthHistory();
        history.Record(Sample(60, 5_000), TimeSpan.FromMinutes(15));
        Assert.False(history.Record(Sample(30, 1_000), TimeSpan.FromMinutes(15)));
        Assert.Single(history.Samples);
    }

    [Fact]
    public void OnlyACompleteFullBellScanBecomesAPoint()
    {
        Assert.Null(WealthHistory.FromValuation(Valuation(isComplete: false, isFullBell: true), Start));
        Assert.Null(WealthHistory.FromValuation(Valuation(isComplete: true, isFullBell: false), Start));

        var sample = WealthHistory.FromValuation(Valuation(isComplete: true, isFullBell: true), Start);
        Assert.NotNull(sample);
        Assert.Equal(500ul, sample.Gil);
        Assert.Equal(9_000ul, sample.Total);
    }

    [Fact]
    public void AWindowReportsItsChangeAndSpan()
    {
        var history = new WealthHistory();
        var interval = TimeSpan.FromMinutes(15);
        history.Record(Sample(0, 1_000), interval);
        history.Record(Sample(60, 4_000), interval);
        history.Record(Sample(120, 3_000), interval);

        var change = history.ChangeOver(TimeSpan.FromHours(24), Start.AddMinutes(120));
        Assert.NotNull(change);
        Assert.Equal(2_000, change.Value.Change);
        Assert.Equal(TimeSpan.FromMinutes(120), change.Value.Span);

        // A window holding fewer than two points has nothing to compare.
        Assert.Null(history.ChangeOver(TimeSpan.FromMinutes(30), Start.AddMinutes(120)));
        // Losses report as negative rather than underflowing the unsigned totals.
        Assert.True(history.ChangeOver(TimeSpan.FromMinutes(90), Start.AddMinutes(120))!.Value.Change < 0);
    }

    [Fact]
    public void ALongHistoryThinsWithoutLosingWhereItStarted()
    {
        var history = new WealthHistory();
        var interval = TimeSpan.FromMinutes(15);
        for (var i = 0; i <= WealthHistory.MaximumSamples * 2; i++)
            history.Record(Sample(i * 20, (ulong)(1_000 + i)), interval);

        Assert.InRange(history.Samples.Count, 2, WealthHistory.MaximumSamples);
        Assert.Equal(Start, history.Samples[0].At);
        Assert.Equal((ulong)(1_000 + WealthHistory.MaximumSamples * 2), history.Samples[^1].Total);
        // Still ordered after thinning, or the plot would zigzag in time.
        Assert.Equal(history.Samples.OrderBy(x => x.At), history.Samples);
    }

    [Fact]
    public void StoredSamplesAreRestoredInOrder()
    {
        var history = new WealthHistory([Sample(60, 2_000), Sample(0, 1_000)]);
        Assert.Equal([1_000ul, 2_000ul], history.Samples.Select(x => x.Total));
    }

    private static PortfolioValuation Valuation(bool isComplete, bool isFullBell) => new(
        Start, Start, isFullBell, isComplete, 1, 1, 1, 1,
        GrossAskingValue: 10_000,
        EstimatedGrossValue: 9_000,
        EstimatedNetAtAsking: 9_500,
        EstimatedNetMarketAligned: 8_500,
        EstimatedMarkdown: 1_000,
        PlayerGil: 500,
        RetainerGil: 0,
        CurrentGil: 500,
        ProjectedWealthAtAsking: 10_000,
        ProjectedWealthMarketAligned: 9_000,
        LiveEstimatedListings: 1,
        Retainers: []);
}
