using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;
using Route = SmartUndercutBot.Core.Tests.Automation.ProcurementControllerTests.Route;

namespace SmartUndercutBot.Core.Tests.Automation;

/// <summary>
/// Decision 5 of the 2026-11-01 review: a trip goes straight to the few worlds the
/// price memory shows holding stock cheap enough to buy, and a full circuit of every
/// world runs only when memory knows too few of them, at most every six hours.
/// </summary>
public sealed class TargetedTripTests
{
    [Fact]
    public void ATripGoesStraightToTheWorldsMemoryShowsHoldingCheapStock()
    {
        using var run = Targeted(("Gilgamesh", 1_000u), ("Behemoth", 1_100u), ("Faerie", 1_050u));
        Complete(run);

        // Its own data centre first, best first, then the next data centre.
        Assert.Equal(new[] { "Gilgamesh", "Faerie", "Behemoth" }, AwayWorldsVisited(run));
        Assert.Contains(run.Log.Messages, x => x.Contains("TARGETED TRIP"));
        Assert.True(run.Game.Purchases > 0);
    }

    [Fact]
    public void ATargetedTripLeavesTheFullCircuitsRouteAndCursorAlone()
    {
        using var run = Targeted(("Gilgamesh", 1_000u), ("Behemoth", 1_100u), ("Faerie", 1_050u));
        string[] saved = ["Exodus", "Lamia", "Ultros"];
        run.Config.Current.PriorityScoutRoute = [.. saved];
        run.Config.Current.PriorityNextWorld = "Lamia";
        Complete(run);

        Assert.Equal(saved, run.Config.Current.PriorityScoutRoute);
        Assert.Equal("Lamia", run.Config.Current.PriorityNextWorld);
    }

    [Fact]
    public void ListingsTooDearForTheMarginBarDoNotMakeATarget()
    {
        // 1,800 against a 2,000 home price is under the 14% bar once both taxes are paid.
        using var run = Targeted(("Gilgamesh", 1_000u), ("Behemoth", 1_100u), ("Faerie", 1_800u));
        Assert.Equal(new[] { "Gilgamesh", "Behemoth" }, run.Controller.TripTargets().Select(x => x.World).ToArray());
    }

    [Fact]
    public void TooLittleInMemoryRunsAFullCircuitThatIsThenNotRepeatedForSixHours()
    {
        using var run = Targeted(("Gilgamesh", 1_000u));
        run.Controller.RunNow();
        for (var i = 0; i < 20 && run.Game.Commands.Count(IsTravel) == 0; i++) run.Tick(2);

        Assert.Contains(run.Log.Messages, x => x.Contains("FULL CIRCUIT"));
        Assert.Equal(run.Now, run.Config.Current.LastFullCircuitAt!.Value, TimeSpan.FromMinutes(1));
        Assert.Equal(run.Now.AddHours(6), run.Controller.NextFullCircuitAllowedAt!.Value, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ThinEvidenceAfterARecentFullCircuitStillAimsAtWhatIsKnown()
    {
        using var run = Targeted(("Gilgamesh", 1_000u));
        run.Config.Current.LastFullCircuitAt = run.Now.AddHours(-1);
        Complete(run);

        Assert.Equal(new[] { "Gilgamesh" }, AwayWorldsVisited(run));
    }

    [Fact]
    public void AScheduledTripWithNothingWorthVisitingStaysHomeWithoutScanning()
    {
        using var run = Targeted();
        Schedule(run);
        run.Config.Current.LastFullCircuitAt = run.Now.AddHours(-1);
        for (var i = 0; i < 5; i++) run.Tick(2);

        Assert.Equal(0, run.Game.Scans);
        Assert.False(run.Controller.IsActive);
    }

    [Fact]
    public void AScheduledTripWithNothingInMemoryRunsAFullCircuitOnceItIsDue()
    {
        using var run = Targeted();
        Schedule(run);
        run.Config.Current.LastFullCircuitAt = run.Now.AddHours(-7);
        run.Tick();

        Assert.Equal(1, run.Game.Scans);
        Assert.True(run.Controller.IsActive);
    }

    [Fact]
    public void AnExplicitShopNowTravelsEvenWithNothingInMemory()
    {
        using var run = Targeted();
        run.Config.Current.LastFullCircuitAt = run.Now.AddHours(-1);
        run.Controller.RunNow();
        for (var i = 0; i < 20 && run.Game.Commands.Count(IsTravel) == 0; i++) run.Tick(2);

        Assert.Contains(run.Game.Commands, IsTravel);
        Assert.Contains(run.Log.Messages, x => x.Contains("FULL CIRCUIT"));
    }

    private static Route Targeted(params (string World, uint Price)[] cheap)
    {
        var memory = new PriceMemory();
        var run = new Route(priority: true, memory: memory);
        run.Config.Current.TargetedTripsEnabled = true;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        foreach (var (world, price) in cheap)
            memory.RecordWorld(new WorldPriceSnapshot(world, 1, true, PriceSource.Universalis, run.Now, 1, 99,
                [new RememberedListing(1, 1, price, 99)]));
        memory.RecordHome(new HomeMarketSnapshot(1, true, PriceSource.Universalis, run.Now, 2_000, 5, 495,
            100m, 3m, 2_000, 99));
        var prices = cheap.ToDictionary(x => x.World, x => x.Price);
        run.Game.LiveProvider = (world, item) => world == "Siren"
            ? [new(0, item, 11, 21, 2_000, 99, true, 0)]
            : prices.TryGetValue(world, out var price)
                ? [new(0, item, 10, 20, price, 99, true, (uint)(price * 99 / 20))]
                : [];
        return run;
    }

    private static void Schedule(Route run)
    {
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 0;
        run.Config.Current.ShoppingTripMinimumGil = 0;
        run.Repricing.LastKnownFreeSaleSlots = 10;
        run.Repricing.ListedStock = [];
    }

    private static bool IsTravel(string command) =>
        command.StartsWith("/li ") && !command.StartsWith("/li tp ") && command != "/li mb";

    private static string[] AwayWorldsVisited(Route run) => run.Game.Commands.Where(IsTravel)
        .Select(x => x[4..]).Where(x => x != "Siren").Distinct().ToArray();

    private static void Complete(Route run)
    {
        run.Controller.RunNow();
        for (var i = 0; i < 3000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
    }
}
