using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// The personal sell-through measurement.
///
/// This is instrumentation, not a decision input - nothing in the planner reads it.
/// These tests exist to prove the safeguards hold before anything ever does, because
/// the failure modes here are silent: a confounded reading would quietly inflate the
/// apparent capture rate, and that is the direction that would make the bot buy more.
/// </summary>
public sealed class SellThroughObserverTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SellThroughObservation Feed(
        SellThroughObservation state, (int Day, ulong Units, ulong Purchased)[] readings, decimal market = 100m)
    {
        foreach (var reading in readings)
            state = SellThroughObserver.Observe(
                state, reading.Units, reading.Purchased, Start.AddDays(reading.Day), market);
        return state;
    }

    [Fact]
    public void TheFirstReadingOnlyEstablishesABaseline()
    {
        var state = SellThroughObserver.Observe(new(), 300, 0, Start, 100m);

        Assert.Equal(0, state.Samples);
        Assert.Equal(0m, state.UnitsPerDay);
        Assert.Equal(300ul, state.LastUnits);
        Assert.Equal(Start, state.LastObservedAt);
        Assert.False(SellThroughObserver.IsReliable(state));
    }

    [Fact]
    public void RestockingIsNotNegativeSales()
    {
        // Held 300, bought 99, now holding 350: 49 units left, not "gained 50".
        var state = Feed(new(), [(0, 300, 0), (1, 350, 99)]);

        Assert.Equal(1, state.Samples);
        Assert.Equal(49m, state.UnitsPerDay);
    }

    [Fact]
    public void SparseHistoryIsNeverTreatedAsEvidence()
    {
        var state = Feed(new(), [(0, 300, 0), (1, 260, 0), (2, 220, 0)]);

        Assert.Equal(2, state.Samples);
        Assert.True(state.UnitsPerDay > 0);
        // Two windows is not a measurement. Nothing may be concluded from it.
        Assert.False(SellThroughObserver.IsReliable(state));
        Assert.Null(SellThroughObserver.CaptureShare(state, 100m));
    }

    [Fact]
    public void EnoughAgreeingWindowsProduceAConservativeCaptureShare()
    {
        // Forty units a day out of a hundred on the board.
        var state = Feed(new(), [(0, 400, 0), (1, 360, 0), (2, 320, 0), (3, 280, 0), (4, 240, 0)]);

        Assert.Equal(4, state.Samples);
        Assert.Equal(4m, state.ObservedDays);
        Assert.True(SellThroughObserver.IsReliable(state));
        Assert.Equal(40m, state.UnitsPerDay);
        Assert.Equal(0.4m, SellThroughObserver.CaptureShare(state, 100m));
    }

    [Fact]
    public void OneBurstMovesTheEstimateButCannotPinItHigh()
    {
        var steady = Feed(new(), [(0, 400, 0), (1, 380, 0), (2, 360, 0), (3, 340, 0), (4, 320, 0)]);
        Assert.Equal(20m, steady.UnitsPerDay);

        // A single day where almost everything moves.
        var afterBurst = SellThroughObserver.Observe(steady, 220, 0, Start.AddDays(5), 100m);
        Assert.True(afterBurst.UnitsPerDay < 50m, $"burst moved it to {afterBurst.UnitsPerDay}");

        // And it decays back as normal days resume, rather than staying inflated.
        var settled = Feed(afterBurst, [(6, 200, 0), (7, 180, 0), (8, 160, 0), (9, 140, 0)]);
        Assert.True(settled.UnitsPerDay < afterBurst.UnitsPerDay);
        Assert.True(settled.UnitsPerDay < 30m);
    }

    [Fact]
    public void ARateAboveTheWholeMarketIsCappedRatherThanBelieved()
    {
        // 200 units gone in a day in a market that only moves 100. The difference
        // cannot all have been sales, so it is capped instead of trusted.
        var state = Feed(new(), [(0, 400, 0), (1, 200, 0)], market: 100m);

        Assert.Equal(100m, state.UnitsPerDay);

        // Sustained, it converges on the market rate and stops there - a share of a
        // market can never exceed the whole of it.
        var sustained = Feed(state, [(2, 200, 200), (3, 200, 200), (4, 200, 200)], market: 100m);
        Assert.Equal(100m, sustained.UnitsPerDay);
        Assert.Equal(1m, SellThroughObserver.CaptureShare(sustained, 100m));

        // Running the position down instead lowers the estimate, as it should.
        var declining = Feed(state, [(2, 100, 0), (3, 50, 0), (4, 10, 0)], market: 100m);
        Assert.True(declining.UnitsPerDay < 100m);
        Assert.True(SellThroughObserver.CaptureShare(declining, 100m) < 1m);
    }

    [Theory]
    // Too short to mean anything.
    [InlineData(0.1)]
    // Too long: the window spans changes we cannot attribute.
    [InlineData(30)]
    public void WindowsOutsideTheUsableRangeAreDiscarded(double days)
    {
        var baseline = SellThroughObserver.Observe(new(), 300, 0, Start, 100m);
        var state = SellThroughObserver.Observe(baseline, 100, 0, Start.AddDays(days), 100m);

        Assert.Equal(0, state.Samples);
        Assert.Equal(0m, state.UnitsPerDay);
        // The baseline still moves forward, so the next window measures from here.
        Assert.Equal(100ul, state.LastUnits);
        Assert.Equal(Start.AddDays(days), state.LastObservedAt);
    }

    [Fact]
    public void AnUnexplainedGainTeachesNothingRatherThanCountingAsNegativeSales()
    {
        // More stock than we can account for - a reading we do not understand.
        var state = Feed(new(), [(0, 100, 0), (1, 400, 0)]);

        Assert.Equal(0, state.Samples);
        Assert.Equal(0m, state.UnitsPerDay);
        Assert.Equal(400ul, state.LastUnits);
    }

    [Fact]
    public void AnEmptyPositionCannotManufactureASample()
    {
        var state = Feed(new(), [(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0)]);

        Assert.Equal(0, state.Samples);
        Assert.False(SellThroughObserver.IsReliable(state));
    }

    [Fact]
    public void CaptureShareNeverCollapsesAMarketNorExceedsIt()
    {
        // A run of days where almost nothing of ours sold.
        var pessimistic = Feed(new(), [(0, 400, 0), (1, 399, 0), (2, 398, 0), (3, 397, 0), (4, 396, 0)]);
        Assert.True(SellThroughObserver.IsReliable(pessimistic));

        // The floor stops sparse or unlucky personal history erasing a line's sizing.
        var share = SellThroughObserver.CaptureShare(pessimistic, 100m);
        Assert.Equal(SellThroughObserver.MinimumCaptureShare, share);
        Assert.True(share >= 0.25m && share <= 1m);

        // And with no market rate there is nothing to take a share of.
        Assert.Null(SellThroughObserver.CaptureShare(pessimistic, 0m));
    }
}
