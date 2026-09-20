using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Services;
using Xunit;
using Route = SmartUndercutBot.Core.Tests.Automation.ProcurementControllerTests.Route;

namespace SmartUndercutBot.Core.Tests.Automation;

/// <summary>
/// Same-item stacks are bought from one search instead of repeating the whole
/// item lookup between purchases. This is an execution-speed change only: every
/// existing buying rule is still applied per stack, so these tests check both that
/// the searches collapse and that nothing extra gets bought.
/// </summary>
public sealed class BatchPurchaseTests
{
    [Fact]
    public void SeveralStacksAreBoughtFromOneSearchWithEachConfirmedBeforeTheNext()
    {
        using var run = Batch(5, 25);
        Complete(run);

        Assert.Equal(5, run.Game.Purchases);
        Assert.Equal(125, run.Game.Inventory);
        // The circuit prices Cactuar once while scouting and opens it once more to
        // buy. Five stacks then come out of that single buying search.
        Assert.Equal(2, run.Game.Searches.Count(x => x.World == "Cactuar"));
        Assert.Equal(5, run.Game.Bought.Count);
        // Each stack was confirmed and accounted for before the next was submitted.
        Assert.Contains(run.Log.Messages, x => x.Contains("Stack 2 of this search"));
        Assert.Contains(run.Log.Messages, x => x.Contains("Stack 5 of this search"));
        Assert.All(run.Log.Messages.Where(x => x.Contains("Stack ") && x.Contains("of this search")),
            x => Assert.Contains("continuing from the loaded listings", x));
    }

    [Fact]
    public void OnlyTheAuthorizedNumberOfStacksIsBoughtWhenMoreAreAvailable()
    {
        // Five eligible stacks on the board, but the trip budget authorises three.
        // A stack costs 1,799 x 25 plus 2,248 tax = 47,223 gil.
        using var run = Batch(5, 25);
        run.Config.Current.ReinvestAvailableGil = false;
        run.Config.Current.ProcurementBudget = 150_000;
        Complete(run);

        Assert.Equal(3, run.Game.Purchases);
        Assert.Equal(75, run.Game.Inventory);
        Assert.InRange(run.Controller.Status.GilSpent, 1u, 150_000u);
    }

    [Fact]
    public void AConfirmedRowThatStaysVisibleIsExcludedAndTheNextListingIsSelected()
    {
        // The fake never removes purchased rows, so every selection must skip the
        // consumed listing ids rather than buying the same stack again.
        using var run = Batch(3, 25);
        Complete(run);

        Assert.Equal(3, run.Game.Purchases);
        // Three distinct listings, each bought exactly once.
        Assert.Equal([1ul, 2ul, 3ul], run.Game.BoughtListingIds.Order().ToArray());
    }

    [Fact]
    public void DisappearingRowsAndReorderedIndicesNeitherDeadlockNorBuyTheWrongRow()
    {
        using var run = Batch(3, 25);
        // The client drops each purchased row and reindexes the remainder, which is
        // what would strand a readiness check tied to the original row count.
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => Enumerable.Range(1, 3)
                .Where(i => !run.Game.BoughtListingIds.Contains((ulong)i))
                .Select((i, index) => new LivePurchaseListing(index, item, (ulong)i, (ulong)i,
                    1799, 25, true, (uint)decimal.Floor(1799 * 25 * 0.05m)))
                .ToArray(),
            _ => [],
        };
        Complete(run);

        Assert.Equal(3, run.Game.Purchases);
        Assert.Equal(3, run.Game.BoughtListingIds.Distinct().Count());
        Assert.Equal(75, run.Game.Inventory);
    }

    [Fact]
    public void ALessAttractiveReplacementAboveTheComparedPriceIsNotBought()
    {
        using var run = Batch(1, 25);
        // After the planned stack is taken, only a dearer listing remains. It may
        // still clear ROI, but the compared plan authorised the observed price.
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => [new(0, item, 1, 1, 1799, 25, true, 2248),
                .. (run.Game.Purchases > 0
                    ? new[] { new LivePurchaseListing(1, item, 2, 2, 2_100, 25, true, 2_625) }
                    : [])],
            _ => [],
        };
        Complete(run);

        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(25, run.Game.Inventory);
    }

    [Fact]
    public void ASoldOutListingRecoversWithoutAnEndlessSearchLoop()
    {
        using var run = Batch(3, 25);
        // The second stack vanishes between purchases: someone else bought it.
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => run.Game.Purchases == 0
                ? Enumerable.Range(1, 3).Select(i => new LivePurchaseListing(i - 1, item, (ulong)i, (ulong)i,
                    1799, 25, true, (uint)decimal.Floor(1799 * 25 * 0.05m))).ToArray()
                : [],
            _ => [],
        };
        Complete(run);

        Assert.Equal(1, run.Game.Purchases);
        // Recovery is bounded: it does not keep researching the empty board.
        Assert.True(run.Game.Searches.Count(x => x.World == "Cactuar") <= 4,
            $"expected bounded recovery, saw {run.Game.Searches.Count(x => x.World == "Cactuar")} searches");
    }

    [Fact]
    public void AnUnknownOutcomeHaltsWithoutASecondSubmission()
    {
        using var run = Batch(5, 25);
        run.Game.AutomaticPurchaseConfirmation = false;
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++) run.Tick(2);

        Assert.True(run.Controller.RequiresManualRestart);
        Assert.Equal(1, run.Game.Purchases);
        run.Tick(3600);
        Assert.Equal(1, run.Game.Purchases);
    }

    [Fact]
    public void AnExpiredSessionForcesAFreshValidatedSearch()
    {
        using var run = Batch(3, 25);
        run.Game.SessionReuseEnabled = false;
        Complete(run);

        // Still correct, just slower: one search per stack when reuse is refused.
        Assert.Equal(3, run.Game.Purchases);
        Assert.True(run.Game.Searches.Count(x => x.World == "Cactuar") >= 3);
    }

    [Fact]
    public void ClosingTheBoardMidBatchStopsFurtherPurchases()
    {
        using var run = Batch(5, 25);
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++)
        {
            if (run.Game.Purchases == 2) run.Game.BoardOpen = false;
            run.Tick(2);
        }

        Assert.Equal(2, run.Game.Purchases);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
    }

    [Fact]
    public void ScoutingStillOnlyComparesAndNeverBuys()
    {
        using var run = Batch(5, 25);
        run.Controller.RunNow();
        // Drive the circuit until the comparison pass opens the buying world. No
        // purchase may happen while the route is still pricing worlds.
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++)
        {
            if (run.Game.Searches.Count < 3)
                Assert.Equal(0, run.Game.Purchases);
            run.Tick(2);
        }
        Assert.Equal(5, run.Game.Purchases);
        Assert.Contains(run.Log.Messages, x => x.Contains("PRICE CHECK"));
    }

    // Demand coverage and bag capacity stopping a batch part-way are covered by
    // BargainSweepTests.ExistingStockCountsAgainstTheBulkDemandTarget and
    // ActualBagReserveStopsTheSweep, which now exercise the reused-session path.

    private static Route Batch(int listings, uint quantity)
    {
        var run = new Route(priority: true);
        var config = run.Config.Current;
        config.ContinueShoppingWhenStocked = true;
        config.ProcurementRules[0].PreferredStock = true;
        config.ProcurementRules[0].BagReserveQuantity = 0;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        run.Game.Gil = 10_000_000;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.DemandMarkets = [new(1, "Popcorn", [], Enumerable.Range(1, 3)
            .Select(i => new ProcurementSale(2700, 700, true, DateTimeOffset.UtcNow.AddDays(-i))).ToArray(),
            HqSalesPerDay: 700m)];
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => Enumerable.Range(1, listings).Select(i => new LivePurchaseListing(i - 1, item,
                (ulong)i, (ulong)i, 1799, quantity, true, (uint)decimal.Floor(1799 * quantity * 0.05m))).ToArray(),
            _ => [],
        };
        return run;
    }

    private static void Complete(Route run)
    {
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
    }
}
