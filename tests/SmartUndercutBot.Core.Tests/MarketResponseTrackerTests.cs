using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class MarketResponseTrackerTests
{
    private static IEnumerable<ulong> Ids(int first, int count) =>
        Enumerable.Range(first, count).Select(x => (ulong)x);

    // Polls once to register the board, lets the short settle elapse, then asks again.
    private static bool Settled(MarketResponseTracker tracker, Clock time, uint item, int rows)
    {
        tracker.IsReady(item, rows);
        time.Advance(400);
        return tracker.IsReady(item, rows);
    }

    [Fact]
    public void AnEmptyBoardIsOnlyBelievedAfterSustainedSilence()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        time.Advance(10_000);
        Assert.False(tracker.IsReady(42, 0));
        tracker.ReceiveCount(42, 0, 0);
        // The old rule finished here, 750 ms after a zero. Since the 2026-09-17
        // hotfix that is how two thirds of reads came back empty.
        time.Advance(750);
        Assert.False(tracker.IsReady(42, 0));
        time.Advance(1_000);
        Assert.False(tracker.IsReady(42, 0));
        time.Advance(750);
        Assert.True(tracker.IsReady(42, 0));
        Assert.Contains("stayed empty", tracker.Describe());
    }

    [Fact]
    public void ALaterDeclaredCountCannotBeShrunkByAnEarlierZero()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 0, 0);
        time.Advance(1_000);
        tracker.ReceiveCount(42, 3, 0);
        // Rows are owed, so a quiet empty board is not a finished one.
        time.Advance(10_000);
        Assert.False(tracker.IsReady(42, 0));
        tracker.ReceiveRows(10, 42, Ids(1, 3), 3);
        Assert.True(Settled(tracker, time, 42, 3));
        Assert.False(tracker.IsPartial(42));
    }

    [Fact]
    public void AZeroArrivingAfterTheRealCountDoesNotEmptyTheBoard()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 3, 0);
        tracker.ReceiveCount(42, 0, 0);
        time.Advance(5_000);
        Assert.False(tracker.IsReady(42, 0));
        tracker.ReceiveRows(10, 42, Ids(1, 3), 3);
        Assert.True(Settled(tracker, time, 42, 3));
    }

    [Fact]
    public void DeclaredRowsOnTheBoardCompleteAfterOnlyAShortSettle()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 3, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 2), 2);
        Assert.False(tracker.IsReady(42, 2));
        tracker.ReceiveRows(10, 42, Ids(3, 1), 1);
        Assert.False(tracker.IsReady(42, 3));
        time.Advance(400);
        Assert.True(tracker.IsReady(42, 3));
        Assert.False(tracker.IsReady(43, 3));
    }

    [Fact]
    public void AStreamStillArrivingWhenTheDeclaredCountIsMetIsWaitedFor()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        // Each stream declares its own fifty. The dearer one happens to land first.
        tracker.ReceiveCount(42, 50, 0);
        for (var page = 0; page < 5; page++)
            tracker.ReceiveRows(11, 42, Ids(51 + page * 10, 10), 10);
        Assert.False(tracker.IsReady(42, 50));
        time.Advance(200);
        tracker.ReceiveCount(42, 50, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.False(tracker.IsReady(42, 60));
        time.Advance(200);
        // Finishing on the first full stream would have read the board without
        // its cheapest rows - exactly the inflated resale anchor of 2026-09-20.
        Assert.False(tracker.IsReady(42, 60));
        time.Advance(200);
        Assert.True(tracker.IsReady(42, 60));
        Assert.Contains("2 request id(s)", tracker.Describe());
    }

    [Fact]
    public void RowsFromASecondStreamCount()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 15, 0);
        // The first id seen used to be the only one accepted, and the board was
        // then filtered down to its rows - the cheapest stream was the one lost.
        tracker.ReceiveRows(11, 42, Ids(11, 5), 5);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.True(tracker.Contains(1));
        Assert.True(tracker.Contains(15));
        Assert.True(Settled(tracker, time, 42, 15));
        Assert.Contains("2 request id(s)", tracker.Describe());
    }

    [Fact]
    public void WithoutAUsableCountAFullPageWaitsLongerThanAShortOne()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 0, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.False(tracker.IsReady(42, 10));
        time.Advance(400);
        // A full page may be followed by another.
        Assert.False(tracker.IsReady(42, 10));
        tracker.ReceiveRows(10, 42, Ids(11, 4), 4);
        Assert.False(tracker.IsReady(42, 14));
        time.Advance(400);
        // A short page ends its stream.
        Assert.True(tracker.IsReady(42, 14));
        Assert.Contains("short final page", tracker.Describe());
    }

    [Fact]
    public void AFullPageWithNoFollowUpCompletesAfterItsQuietWindow()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.False(tracker.IsReady(42, 10));
        time.Advance(1_400);
        Assert.False(tracker.IsReady(42, 10));
        time.Advance(100);
        Assert.True(tracker.IsReady(42, 10));
    }

    [Fact]
    public void ABoardStillFillingKeepsResettingTheQuietWindow()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 0, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.False(tracker.IsReady(42, 4));
        time.Advance(1_000);
        Assert.False(tracker.IsReady(42, 8));
        time.Advance(1_000);
        Assert.False(tracker.IsReady(42, 10));
        time.Advance(1_000);
        Assert.False(tracker.IsReady(42, 10));
        time.Advance(500);
        Assert.True(tracker.IsReady(42, 10));
    }

    [Fact]
    public void AShortfallWaitsLongerAndIsFlaggedPartial()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 100, 0);
        for (var page = 0; page < 5; page++)
            tracker.ReceiveRows(10, 42, Ids(page * 10 + 1, 10), 10);
        Assert.False(tracker.IsReady(42, 50));
        time.Advance(1_500);
        Assert.False(tracker.IsReady(42, 50));
        Assert.False(tracker.IsComplete(42));
        Assert.Null(tracker.CompletedAt(42));
        Assert.False(tracker.IsReusable(42, TimeSpan.FromMinutes(1)));
        time.Advance(1_500);
        // The rest is not coming. The cheapest rows arrive first, so the board
        // still prices the market; it is only marked as not saying how deep it is.
        Assert.True(tracker.IsReady(42, 50));
        Assert.True(tracker.IsPartial(42));
        Assert.Contains("50 of 100", tracker.Describe());
    }

    [Fact]
    public void AFullBoardCompletesAtOnce()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.True(tracker.IsReady(42, MarketResponseTracker.MaximumRows));
    }

    [Fact]
    public void PagesTheBoardDoesNotShowNeverReadAsAnEmptyMarket()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveRows(10, 42, Ids(1, 3), 3);
        time.Advance(30_000);
        Assert.False(tracker.IsReady(42, 0));
    }

    [Fact]
    public void RowsLeftOnTheBoardBeforeTheServerAnswersProveNothing()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        time.Advance(30_000);
        Assert.False(tracker.IsReady(42, 20));
    }

    [Fact]
    public void ACountIsCreditedWhenTheHandlerSetsTheSearchItem()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(7, 7, 3, 0);
        Assert.Contains("1 for another item", tracker.Describe());
        tracker.ReceiveCount(7, 42, 3, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 3), 3);
        Assert.True(Settled(tracker, time, 42, 3));
    }

    [Fact]
    public void AnEmptyPageEndsAStreamAlreadySeen()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveRows(10, 42, Ids(1, 10), 10);
        Assert.False(tracker.IsReady(42, 10));
        // An empty page from an unknown stream says nothing about ours.
        tracker.ReceiveRows(99, 0, [], 0);
        time.Advance(400);
        Assert.False(tracker.IsReady(42, 10));
        tracker.ReceiveRows(10, 0, [], 0);
        time.Advance(400);
        Assert.True(tracker.IsReady(42, 10));
    }

    [Fact]
    public void RemovingAPurchasedRowDoesNotUnFinishACompleteResponse()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 3, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 3), 3);
        Assert.True(Settled(tracker, time, 42, 3));
        // The client drops the row we just bought. The response is still the
        // complete one the server sent; re-deriving readiness from the shrunken
        // board would wait for rows that will never come back.
        Assert.True(tracker.IsReady(42, 2));
        Assert.True(tracker.IsReady(42, 0));
        Assert.True(tracker.IsComplete(42));
    }

    [Fact]
    public void ANewRequestClearsTheCompletedResponse()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 1, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 1), 1);
        Assert.True(Settled(tracker, time, 42, 1));
        var generation = tracker.Generation;

        tracker.Begin(42);
        Assert.False(tracker.IsComplete(42));
        Assert.False(tracker.IsReady(42, 1));
        Assert.NotEqual(generation, tracker.Generation);
        Assert.Equal(42u, tracker.CurrentItem);
        Assert.NotNull(tracker.RequestAge(42));
        Assert.Null(tracker.RequestAge(43));
    }

    [Fact]
    public void ReReadingCachedRowsDoesNotMakeTheObservationFreshAgain()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 1, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 1), 1);
        Assert.True(Settled(tracker, time, 42, 1));
        var observed = tracker.CompletedAt(42);

        time.Advance(60_000);
        Assert.True(tracker.IsReady(42, 1));
        // The stamp is the network observation, not the last read.
        Assert.Equal(observed, tracker.CompletedAt(42));
        Assert.True(tracker.IsReusable(42, TimeSpan.FromSeconds(90)));
        Assert.False(tracker.IsReusable(42, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AnErrorStopsReuseEvenAfterACompleteResponse()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 1, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 1), 1);
        Assert.True(Settled(tracker, time, 42, 1));

        tracker.ReceiveCount(42, 1, 5);
        Assert.False(tracker.IsReady(42, 1));
        Assert.False(tracker.IsComplete(42));
        Assert.False(tracker.IsReusable(42, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void LatePacketsFromThePreviousSearchCannotCompleteTheNextPurchase()
    {
        var time = new Clock();
        var tracker = new MarketResponseTracker(time);
        tracker.Begin(42);
        tracker.ReceiveCount(42, 1, 0);
        tracker.ReceiveRows(10, 42, Ids(1, 1), 1);
        tracker.Begin(0);
        tracker.Begin(42);
        // Stream 10 belonged to the abandoned request; its late pages are noise.
        tracker.ReceiveRows(10, 42, Ids(1, 1), 1);
        time.Advance(5_000);
        Assert.False(tracker.IsReady(42, 1));
        Assert.False(tracker.Contains(1));
        tracker.ReceiveRows(11, 42, Ids(2, 1), 1);
        Assert.False(tracker.IsReady(42, 1));
        time.Advance(400);
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
