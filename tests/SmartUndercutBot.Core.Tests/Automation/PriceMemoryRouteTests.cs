using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;
using Route = SmartUndercutBot.Core.Tests.Automation.ProcurementControllerTests.Route;

namespace SmartUndercutBot.Core.Tests.Automation;

/// <summary>
/// The production shopping controller with a durable price memory: live reads are
/// remembered, a reload keeps what the last circuit saw, and the review fixes to
/// the route and the early-stop path hold.
/// </summary>
public sealed class PriceMemoryRouteTests
{
    [Fact]
    public void EveryLiveBoardReadIsRememberedPerWorld()
    {
        var memory = new PriceMemory();
        using var run = new Route(priority: true, memory: memory);
        run.Config.Current.PriorityWorldsPerTrip = 3;
        run.Game.AutomaticWorldArrival = true;
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        Complete(run);

        var data = memory.ToData();
        Assert.Contains(data.Worlds, x => x.World == "Siren" && x.ItemId == 1 && x.Source == PriceSource.LiveBoard);
        Assert.True(data.Worlds.Count(x => x.World != "Siren") >= 3);
        var away = data.Worlds.First(x => x.World != "Siren");
        Assert.Equal(2_000u, away.Lowest);
        Assert.Equal(10UL, away.Cheapest[0].ListingId);
        // The home read also becomes a home-market reading.
        Assert.Contains(data.Home, x => x.ItemId == 1 && x.IsHighQuality && x.LowestCompetitor == 2_000);
    }

    [Fact]
    public void AReloadRestoresWhatTheLastCircuitSawInsteadOfRereadingIt()
    {
        var memory = new PriceMemory();
        using (var first = new Route(priority: true, memory: memory))
        {
            first.Config.Current.PriorityWorldsPerTrip = 4;
            first.Game.AutomaticWorldArrival = true;
            first.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
            Complete(first);
            Assert.True(first.Controller.ScoutKnowledgeCoverage.Known >= 4);
        }

        // A fresh controller is a plugin reload: nothing in RAM, the same memory on
        // disk, an hour later - well inside the 24-hour scout lifetime.
        using var second = new Route(priority: true, memory: memory, startOffsetSeconds: 2 * 3600);
        second.Config.Current.PriorityWorldsPerTrip = 4;
        second.Game.AutomaticWorldArrival = true;
        second.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        Complete(second);
        Assert.Contains(second.Log.Messages, x => x.Contains("PRICE MEMORY: restored"));
        // The worlds the first circuit already priced are not searched again.
        Assert.True(second.Controller.ScoutKnowledgeCoverage.Known >= 8);
    }

    [Fact]
    public void CachedReadingsNeverEnterTheBuyComparison()
    {
        var memory = new PriceMemory();
        // A cached regional reading of an absurdly cheap stack on Jenova.
        memory.RecordWorld(new WorldPriceSnapshot("Jenova", 1, true, PriceSource.Universalis, DateTimeOffset.UtcNow,
            1, 99, [new(0, 0, 1, 99)]));
        using var run = new Route(priority: true, memory: memory);
        run.Config.Current.PriorityWorldsPerTrip = 2;
        run.Game.AutomaticWorldArrival = true;
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        Complete(run);
        Assert.DoesNotContain(run.Log.Messages, x => x.Contains("PRICE MEMORY: restored"));
        Assert.Equal(0, run.Game.Purchases);
    }

    [Fact]
    public void ACursorPastTheRememberedMarketDoesNotSkipIt()
    {
        using var run = BulkRoute();
        Remember(run, "Jenova");
        Assert.Equal("/li Jenova", FirstTravelCommand(run));
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);

        // A later trip left the cursor eight stops in. The market is still fresh, so
        // the next circuit must open on it rather than resume behind it.
        var route = run.Config.Current.PriorityScoutRoute;
        Assert.Equal("Jenova", route[0]);
        run.Config.Current.PriorityNextWorld = route[8];
        Remember(run, "Jenova");
        run.Game.Commands.Clear();
        run.Repricing.IsActive = false;
        Assert.Equal("/li Jenova", FirstTravelCommand(run));
    }

    [Fact]
    public void TwoUnreadableWorldsAfterScoutingStillCompareWhatWasSeen()
    {
        using var run = new Route(priority: true);
        run.Game.AutomaticWorldArrival = true;
        // The route opens Cactuar > Adamantoise > Faerie: the first away world reads
        // fine, the next two never answer.
        run.Game.BrokenBoardWorlds.Add("Adamantoise");
        run.Game.BrokenBoardWorlds.Add("Faerie");
        run.Game.LiveProvider = (_, item) => [new(0, item, 10, 20, 2000, 99, true, 0)];
        Complete(run);
        Assert.Contains(run.Log.Messages, m => m.Contains("two consecutive worlds were reached"));
        Assert.Contains(run.Log.Messages, m => m.Contains("Stopped early after two unreadable worlds; comparing"));
    }

    private static void Remember(Route run, string world) => run.Config.Current.RememberedBargains =
    [
        new()
        {
            WorldName = world, ItemId = 1, ItemName = "Popcorn", IsHighQuality = true,
            UnitPrice = 1_799, UnitsBought = 650, StopReason = BargainStopReason.OutOfGil,
            ObservedAt = run.Now.AddMinutes(-30),
        },
    ];

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

    private static Route BulkRoute()
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
            _ => [],
        };
        return run;
    }

    private static void Complete(Route run)
    {
        run.Controller.RunNow();
        for (var i = 0; i < 3000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
    }
}
