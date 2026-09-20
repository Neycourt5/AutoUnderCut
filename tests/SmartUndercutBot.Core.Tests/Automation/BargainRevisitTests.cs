using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;
using Route = SmartUndercutBot.Core.Tests.Automation.ProcurementControllerTests.Route;

namespace SmartUndercutBot.Core.Tests.Automation;

/// <summary>
/// A market that was still selling cheaply when the wallet emptied is worth going
/// straight back to. These drive the whole loop: buy until broke, record the world,
/// and open the next circuit there.
/// </summary>
public sealed class BargainRevisitTests
{
    [Fact]
    public void RunningOutOfGilRemembersTheMarketItWasBuyingFrom()
    {
        using var run = BulkRoute(97, 25);
        run.Game.Gil = 1_235_846; // affords 26 of the 97 stacks
        Complete(run);

        var remembered = Assert.Single(run.Config.Current.RememberedBargains);
        Assert.Equal("Cactuar", remembered.WorldName);
        Assert.Equal(1u, remembered.ItemId);
        Assert.Equal(BargainStopReason.OutOfGil, remembered.StopReason);
        Assert.Equal(26u * 25u, remembered.UnitsBought);
        Assert.Equal(1_799u, remembered.UnitPrice);
        Assert.Contains(run.Log.Messages, x => x.Contains("REMEMBERED for the next trip"));
    }

    [Fact]
    public void ASweptMarketIsNotRememberedBecauseItsListingsAreGone()
    {
        // A wallet big enough to clear the board: the sweep completes, so there is
        // nothing left to come back for.
        using var run = BulkRoute(3, 25);
        Complete(run);

        Assert.Equal(3, run.Game.Purchases);
        Assert.Contains(run.Log.Messages, x => x.Contains("BARGAIN SWEEP COMPLETE"));
        Assert.Empty(run.Config.Current.RememberedBargains);
    }

    [Fact]
    public void TheNextTripOpensOnTheRememberedWorld()
    {
        using var run = BulkRoute(97, 25);
        run.Config.Current.RememberedBargains =
        [
            new()
            {
                WorldName = "Jenova", ItemId = 1, ItemName = "Popcorn", IsHighQuality = true,
                UnitPrice = 1_799, UnitsBought = 650, StopReason = BargainStopReason.OutOfGil,
                ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            },
        ];
        // The circuit heads for the remembered market first instead of the world
        // the cached hints would otherwise have ranked highest.
        Assert.Equal("/li Jenova", FirstTravelCommand(run));
        Assert.Equal("Jenova", run.Config.Current.PriorityScoutRoute[0]);
        Assert.Contains(run.Log.Messages, x => x.Contains("SHOPPING FIRST STOPS from the last trip"));
    }

    [Fact]
    public void AStaleMemoryIsIgnoredAndTheNormalRouteResumes()
    {
        using var run = BulkRoute(97, 25);
        run.Config.Current.ScoutKnowledgeMaxAgeHours = 24;
        run.Config.Current.RememberedBargains =
        [
            new()
            {
                WorldName = "Jenova", ItemId = 1, ItemName = "Popcorn", IsHighQuality = true,
                UnitPrice = 1_799, UnitsBought = 650, StopReason = BargainStopReason.OutOfGil,
                ObservedAt = DateTimeOffset.UtcNow.AddDays(-3),
            },
        ];
        // Knowledge older than the scout window is not acted on: the circuit uses
        // its normal hint-ranked order rather than returning to a cold memory.
        Assert.NotEqual("/li Jenova", FirstTravelCommand(run));
        Assert.NotEqual("Jenova", run.Config.Current.PriorityScoutRoute[0]);
        Assert.DoesNotContain(run.Log.Messages, x => x.Contains("SHOPPING FIRST STOPS"));
    }

    /// <summary>Ticks until the route issues a world-travel command, or gives up.</summary>
    private static string FirstTravelCommand(Route run)
    {
        run.Controller.RunNow();
        for (var i = 0; i < 200; i++)
        {
            if (run.Game.Commands.FirstOrDefault(c => c.StartsWith("/li ") && !c.StartsWith("/li mb")) is { } go)
                return go;
            run.Tick(1);
        }
        return string.Empty;
    }

    private static Route BulkRoute(int listings, uint quantity, bool stockJenova = false)
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
            "Cactuar" => Stock(item),
            "Jenova" when stockJenova => Stock(item),
            _ => [],
        };
        return run;

        LivePurchaseListing[] Stock(uint item) => Enumerable.Range(1, listings)
            .Select(i => new LivePurchaseListing(i - 1, item, (ulong)i, (ulong)i, 1799, quantity,
                true, (uint)decimal.Floor(1799 * quantity * 0.05m))).ToArray();
    }

    private static void Complete(Route run)
    {
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
    }
}
