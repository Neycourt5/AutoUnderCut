using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class MarketResponseTrackerTests
{
    [Fact]
    public void EmptyWindowBeforeServerAnswerIsNotReady()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        time.Advance(10000);
        Assert.False(tracker.IsReady(42, 0));
        tracker.ReceiveCount(42, 0, 0);
        Assert.False(tracker.IsReady(42, 0));
        time.Advance(750);
        Assert.True(tracker.IsReady(42, 0));
    }

    [Fact]
    public void EveryPacketAndTheNativeRowsMustArriveAndSettle()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 3, 0);
        tracker.ReceiveRows(10, 42, [1, 2]);
        time.Advance(5000);
        Assert.False(tracker.IsReady(42, 3));
        tracker.ReceiveRows(10, 42, [2, 3]);
        Assert.False(tracker.IsReady(42, 3));
        time.Advance(750);
        Assert.False(tracker.IsReady(42, 2));
        Assert.True(tracker.IsReady(42, 3));
        Assert.False(tracker.IsReady(43, 3));
    }

    [Fact]
    public void LatePacketsFromThePreviousSearchCannotCompleteTheNextPurchase()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 1, 0);
        tracker.ReceiveRows(10, 42, [1]);
        tracker.Begin(0);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 1, 0);
        tracker.ReceiveRows(10, 42, [1]);
        time.Advance(1000);
        Assert.False(tracker.IsReady(42, 1));
        Assert.False(tracker.Contains(1));
        tracker.ReceiveRows(11, 42, [2]);
        time.Advance(750);
        Assert.True(tracker.IsReady(42, 1));
    }

    [Fact]
    public void ServerErrorIsNotAnEmptySuccessfulResponse()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 0, 500);
        time.Advance(10000);
        Assert.False(tracker.IsReady(42, 0));
        Assert.Contains("error 500", tracker.Summary);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int ms) => now = now.AddMilliseconds(ms);
    }
}
