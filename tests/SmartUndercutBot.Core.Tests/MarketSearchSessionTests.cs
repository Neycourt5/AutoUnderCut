using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class MarketSearchSessionTests
{
    [Fact]
    public void SearchPreparesBeforeSubmissionAndOpensOnlyTheExactItem()
    {
        var ui = new SearchUi();
        var clock = new Clock();
        var session = new MarketSearchSession(ui, clock);
        Assert.True(session.Request(42, "General-purpose Pastel Pink Dye"));
        Assert.Equal(new[] { "clear", "prepare" }, ui.Calls);
        Assert.False(session.Poll(42));
        Assert.Equal(0, ui.Submissions);
        clock.Advance(400);
        Assert.False(session.Poll(42));
        Assert.Equal(1, ui.Submissions);
        ui.Rows = [new(0, 100, true), new(1, 42, true)];
        clock.Advance(500);
        Assert.False(session.Poll(42));
        Assert.Equal((1, 42u), Assert.Single(ui.Activations));
        ui.Result = new(true, 42, true);
        Assert.False(session.Poll(42));
        Assert.Contains("Waiting for live prices", session.Status);
        ui.Result = new(true, 42, false);
        Assert.True(session.Poll(42));
        Assert.Contains("Live prices loaded", session.Status);
        Assert.Equal(1, ui.Submissions);
        Assert.Single(ui.Activations);
    }

    [Fact]
    public void RepeatedPollingDoesNotResubmitOrDoubleClick()
    {
        var ui = new SearchUi { Rows = [new(0, 42, true)] };
        var clock = new Clock();
        var session = Started(ui, clock);
        clock.Advance(500);
        session.Poll(42);
        for (var i = 0; i < 100; i++)
        {
            session.Request(42, "Dye");
            session.Poll(42);
        }
        Assert.Equal(1, ui.Submissions);
        Assert.Single(ui.Activations);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(42, false)]
    public void WrongOrDisabledRowsCannotBeSelected(uint id, bool enabled)
    {
        var ui = new SearchUi { Rows = [new(0, id, enabled)] };
        var clock = new Clock();
        var session = Started(ui, clock);
        clock.Advance(500);
        Assert.False(session.Poll(42));
        Assert.Empty(ui.Activations);
        Assert.Contains("other/disabled", session.Status);
    }

    [Fact]
    public void ExistingResultsCannotBeAcceptedBeforeOurRowSelection()
    {
        var ui = new SearchUi();
        var clock = new Clock();
        var session = Started(ui, clock);
        ui.Result = new(true, 42, false);
        Assert.False(session.Poll(42));
        Assert.Equal(1, ui.ClosedResults);
    }

    [Fact]
    public void InFlightStaleResultsAreNotClosedOrAccepted()
    {
        var ui = new SearchUi();
        var clock = new Clock();
        var session = Started(ui, clock);
        ui.Result = new(true, 100, true);
        Assert.False(session.Poll(42));
        Assert.Equal(0, ui.ClosedResults);
        Assert.Empty(ui.Activations);
    }

    [Fact]
    public void SwitchingItemsRequiresAnotherSettledSearch()
    {
        var ui = new SearchUi();
        var clock = new Clock();
        var session = Started(ui, clock);
        Assert.True(session.Request(100, "Different dye"));
        Assert.False(session.Poll(42));
        Assert.False(session.Poll(100));
        Assert.Equal(1, ui.Submissions);
        clock.Advance(400);
        session.Poll(100);
        Assert.Equal(2, ui.Submissions);
    }

    [Fact]
    public void ResetForNextPurchaseForcesFreshSearchEvenForTheSameItem()
    {
        var ui = new SearchUi();
        var clock = new Clock();
        var session = Started(ui, clock);
        session.Reset();
        Assert.False(session.Poll(42));
        session.Request(42, "Dye");
        clock.Advance(400);
        session.Poll(42);
        Assert.Equal(2, ui.Submissions);
    }

    [Fact]
    public void EmptySearchExplainsTheStageWithoutPretendingToHavePrices()
    {
        var ui = new SearchUi();
        var clock = new Clock();
        var session = Started(ui, clock);
        clock.Advance(1000);
        Assert.False(session.Poll(42));
        Assert.Contains("no results are visible", session.Status);
        Assert.Empty(ui.Activations);
    }

    [Fact]
    public void RowChangingBeforeActivationIsRetriedWithoutMarkingItSelected()
    {
        var ui = new SearchUi { Rows = [new(0, 42, true)], AcceptRow = false };
        var clock = new Clock();
        var session = Started(ui, clock);
        clock.Advance(500);
        Assert.False(session.Poll(42));
        ui.Result = new(true, 42, false);
        Assert.False(session.Poll(42));
        Assert.Equal(1, ui.ClosedResults);
    }

    private static MarketSearchSession Started(SearchUi ui, Clock clock)
    {
        var session = new MarketSearchSession(ui, clock);
        session.Request(42, "Dye");
        clock.Advance(400);
        session.Poll(42);
        return session;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int milliseconds) => now = now.AddMilliseconds(milliseconds);
    }

    private sealed class SearchUi : IMarketSearchUi
    {
        public List<string> Calls { get; } = [];
        public List<(int, uint)> Activations { get; } = [];
        public IReadOnlyList<MarketSearchRow> Rows = [];
        public MarketSearchResult Result = new(false, 0, false);
        public int Submissions;
        public int ClosedResults;
        public bool AcceptRow = true;
        public bool PrepareSearch(string name) { Calls.Add("prepare"); return true; }
        public bool SubmitSearch(string name) { Calls.Add("submit"); Submissions++; return true; }
        public IReadOnlyList<MarketSearchRow> ReadRows() => Rows;
        public bool ActivateRow(int index, uint id) { Activations.Add((index, id)); return AcceptRow; }
        public MarketSearchResult ReadResult() => Result;
        public void CloseResult() { ClosedResults++; Result = new(false, 0, false); }
        public void ClearSearch() { Calls.Add("clear"); Result = new(false, 0, false); }
    }
}
