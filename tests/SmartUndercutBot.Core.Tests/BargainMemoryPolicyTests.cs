using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// Running out of gil part-way through a cheap market is the case this exists for:
/// the listings do not disappear because the wallet emptied, so the next circuit
/// should open there instead of rediscovering it twenty stops later.
/// </summary>
public sealed class BargainMemoryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static BargainMemory Entry(string world, uint item = 1, uint price = 1_799, uint units = 500,
        BargainStopReason stop = BargainStopReason.OutOfGil, int minutesAgo = 10) => new()
    {
        WorldName = world, ItemId = item, ItemName = "Caramel Popcorn", IsHighQuality = true,
        UnitPrice = price, UnitsBought = units, StopReason = stop,
        ObservedAt = Now.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void AMarketStoppedByAnEmptyWalletOutranksOneThatSimplyEnded()
    {
        var order = BargainMemoryPolicy.RevisitOrder(
        [
            Entry("Faerie", units: 900, stop: BargainStopReason.TripEnded),
            Entry("Cactuar", units: 500, stop: BargainStopReason.OutOfGil),
        ]);
        // Being cut short is the stronger signal that stock remains, even though
        // Faerie absorbed more capital before the trip moved on.
        Assert.Equal(["Cactuar", "Faerie"], order);
    }

    [Fact]
    public void AmongBlockedMarketsTheOneThatAbsorbedMostCapitalGoesFirst()
    {
        var order = BargainMemoryPolicy.RevisitOrder(
        [
            Entry("Jenova", units: 100),
            Entry("Cactuar", units: 900),
            Entry("Midgardsormr", units: 400),
        ]);
        Assert.Equal(["Cactuar", "Midgardsormr", "Jenova"], order);
    }

    [Fact]
    public void StaleAndMalformedEntriesAreDropped()
    {
        var fresh = BargainMemoryPolicy.Fresh(
        [
            Entry("Cactuar", minutesAgo: 10),
            Entry("Faerie", minutesAgo: 60 * 30),      // older than the window
            Entry("", minutesAgo: 5),                   // no world
            Entry("Jenova", units: 0, minutesAgo: 5),   // nothing was bought
            Entry("Behemoth", minutesAgo: -60),         // stamped in the future
        ], Now, TimeSpan.FromHours(24));
        Assert.Equal(["Cactuar"], fresh.Select(x => x.WorldName));
    }

    [Fact]
    public void ASweptMarketIsForgottenRatherThanRevisited()
    {
        var merged = BargainMemoryPolicy.Merge(
            existing: [Entry("Cactuar"), Entry("Faerie")],
            observed: [],
            exhausted: [("Cactuar", 1u, true)],
            Now, TimeSpan.FromHours(24));
        // Cactuar's listings were bought out, so sending the next trip back there
        // would waste its first stop.
        Assert.Equal(["Faerie"], merged.Select(x => x.WorldName));
    }

    [Fact]
    public void ANewerSightingReplacesTheOlderOneForTheSameMarket()
    {
        var merged = BargainMemoryPolicy.Merge(
            existing: [Entry("Cactuar", price: 1_500, units: 100, minutesAgo: 600)],
            observed: [Entry("Cactuar", price: 1_799, units: 700, minutesAgo: 5)],
            exhausted: [],
            Now, TimeSpan.FromHours(24));
        var only = Assert.Single(merged);
        Assert.Equal(1_799u, only.UnitPrice);
        Assert.Equal(700u, only.UnitsBought);
    }

    [Fact]
    public void TheMemoryIsCappedSoACircuitIsNeverDominatedByIt()
    {
        var many = Enumerable.Range(1, 20)
            .Select(i => Entry($"World{i}", item: (uint)i, units: (uint)(i * 10)))
            .ToArray();
        var merged = BargainMemoryPolicy.Merge([], many, [], Now, TimeSpan.FromHours(24));
        Assert.Equal(BargainMemoryPolicy.MaximumRemembered, merged.Count);
        // The strongest markets survive the trim.
        Assert.Contains(merged, x => x.WorldName == "World20");
        Assert.DoesNotContain(merged, x => x.WorldName == "World1");
    }

    [Fact]
    public void RememberedWorldsLeadTheRouteWhileTheRestKeepsDataCentreBatching()
    {
        string[] worlds = [.. Enumerable.Range(1, 8).Select(i => $"A{i}"),
                           .. Enumerable.Range(1, 8).Select(i => $"B{i}")];
        var scores = worlds.ToDictionary(w => w, _ => 0m);
        var route = ShoppingScoutPolicy.BuildRoute(worlds, "A1", scores, ["B5"]);

        Assert.Equal("B5", route[0]);
        Assert.Equal(worlds.Length - 1, route.Count);      // home excluded, nothing duplicated
        Assert.Equal(route.Count, route.Distinct().Count());
        // Behind the revisit the home data centre still comes before the other one.
        var rest = route.Skip(1).ToArray();
        Assert.All(rest.Take(6), w => Assert.StartsWith("A", w));
    }

    [Fact]
    public void AnUnknownOrHomeRevisitWorldIsIgnoredRatherThanCorruptingTheRoute()
    {
        string[] worlds = ["A1", "A2", "A3"];
        var scores = worlds.ToDictionary(w => w, _ => 0m);
        var route = ShoppingScoutPolicy.BuildRoute(worlds, "A1", scores, ["A1", "Nowhere"]);
        Assert.Equal(["A2", "A3"], route);
    }
}
