using System.Text.Json;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class PriceMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    private static ProcurementMarketListing Row(uint price, uint quantity, bool hq = true, uint item = 49240,
        ulong listing = 0, string world = "Leviathan") =>
        new(item, listing, 7, world, 64, price, quantity, hq);

    [Fact]
    public void KeepsTheCheapestRowsOfTheRightQualityAndCountsTheBoard()
    {
        var memory = new PriceMemory();
        var rows = Enumerable.Range(0, 30).Select(i => Row((uint)(1_500 + i * 10), 99, listing: (ulong)i + 1))
            .Append(Row(900, 99, hq: false)).ToArray();
        memory.RecordWorld("Leviathan", 49240, true, PriceSource.LiveBoard, Now, rows);

        var snapshot = memory.Latest("leviathan", 49240, true, Now, TimeSpan.FromHours(1))!;
        Assert.Equal(30, snapshot.Listings);
        Assert.Equal(2_970UL, snapshot.Units);
        Assert.Equal(PriceMemory.MaximumRowsPerSnapshot, snapshot.Cheapest.Length);
        Assert.Equal(1_500u, snapshot.Lowest);
        Assert.Equal(1UL, snapshot.Cheapest[0].ListingId);
        Assert.Equal(99UL * 3, snapshot.UnitsAtOrBelow(1_520));
    }

    [Fact]
    public void AnOlderReadingNeverReplacesANewerOne()
    {
        var memory = new PriceMemory();
        memory.RecordWorld("Exodus", 49240, true, PriceSource.LiveBoard, Now, [Row(1_900, 99, world: "Exodus")]);
        memory.RecordWorld("Exodus", 49240, true, PriceSource.LiveBoard, Now.AddMinutes(-30), [Row(1_000, 99, world: "Exodus")]);
        Assert.Equal(1_900u, memory.Latest("Exodus", 49240, true, Now, TimeSpan.FromHours(1))!.Lowest);
    }

    [Fact]
    public void FreshWorldsListsTheNewestReadingPerWorldCheapestFirstAndHonoursAge()
    {
        var memory = new PriceMemory();
        memory.RecordWorld("Exodus", 49240, true, PriceSource.Universalis, Now.AddHours(-1), [Row(1_700, 99, world: "Exodus")]);
        memory.RecordWorld("Exodus", 49240, true, PriceSource.LiveBoard, Now.AddMinutes(-10), [Row(1_800, 99, world: "Exodus")]);
        memory.RecordWorld("Marilith", 49240, true, PriceSource.Universalis, Now.AddMinutes(-20), [Row(1_450, 99, world: "Marilith")]);
        memory.RecordWorld("Golem", 49240, true, PriceSource.Universalis, Now.AddHours(-5), [Row(1_000, 99, world: "Golem")]);

        var fresh = memory.FreshWorlds(49240, true, Now, TimeSpan.FromHours(2));
        Assert.Equal(["Marilith", "Exodus"], fresh.Select(x => x.World));
        // The live reading is the newer one on Exodus, so it wins over the cache.
        Assert.Equal(1_800u, fresh[1].Lowest);
        Assert.Empty(memory.FreshWorlds(49240, false, Now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void SalesAreDeduplicatedAndSummarisedPerItem()
    {
        var memory = new PriceMemory();
        var sale = new RetainerSale(Now.AddHours(-1), 49240, true, 99, 272_000, 2, "Caramel Popcorn");
        Assert.True(memory.RecordSale(sale));
        // The same message delivered twice is one sale...
        Assert.False(memory.RecordSale(sale with { At = sale.At.AddMilliseconds(50) }));
        // ...but a buyer taking an identical second stack a couple of seconds later is two.
        Assert.True(memory.RecordSale(sale with { At = sale.At.AddSeconds(2) }));
        Assert.True(memory.RecordSale(sale with { At = sale.At.AddMinutes(20), Quantity = 50, GilAfterFees = 137_000 }));
        Assert.True(memory.RecordSale(new(Now.AddDays(-2), 49234, true, 99, 390_000, 2)));

        var day = memory.SummarizeSales(Now, TimeSpan.FromDays(1));
        var popcorn = Assert.Single(day);
        Assert.Equal(3, popcorn.Sales);
        Assert.Equal(248UL, popcorn.Units);
        Assert.Equal(681_000UL, popcorn.GilAfterFees);
        Assert.Equal("Caramel Popcorn", popcorn.ItemName);
        Assert.Equal(2, memory.SummarizeSales(Now, TimeSpan.FromDays(7)).Count);
    }

    [Fact]
    public void PruneDropsWhatIsTooOldAndBumpsTheRevision()
    {
        var memory = new PriceMemory();
        memory.RecordWorld("Exodus", 1, true, PriceSource.LiveBoard, Now.AddDays(-8), [Row(10, 1, item: 1)]);
        memory.RecordWorld("Exodus", 2, true, PriceSource.LiveBoard, Now.AddDays(-1), [Row(10, 1, item: 2)]);
        memory.RecordSale(new(Now.AddDays(-31), 1, true, 1, 10, 0));
        memory.RecordSale(new(Now.AddDays(-3), 1, true, 1, 10, 0));
        var before = memory.Revision;

        Assert.Equal(2, memory.Prune(Now));
        Assert.True(memory.Revision > before);
        Assert.Equal(1, memory.WorldSnapshotCount);
        Assert.Equal(1, memory.SaleCount);
        Assert.Equal(0, memory.Prune(Now));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var memory = new PriceMemory();
        memory.RecordWorld("Seraph", 49244, true, PriceSource.LiveBoard, Now, [Row(1_500, 99, item: 49244, listing: 42)]);
        memory.RecordHome(new(49244, true, PriceSource.Universalis, Now, 2_997, 20, 743, 374m, 9m, 3_000, 50));
        memory.RecordSale(new(Now, 49244, true, 50, 142_000, 2, "Popoto Potage"));
        memory.ReplaceCandidates([new(49240, "Caramel Popcorn", true, "Meals", 2_081m, 35m, 2_694, 1_450, "Marilith",
            "stable", PriceSource.Saddlebag, Now)], Now);

        var json = JsonSerializer.Serialize(memory.ToData());
        var restored = new PriceMemory(JsonSerializer.Deserialize<PriceMemoryData>(json));

        var world = restored.Latest("Seraph", 49244, true, Now, TimeSpan.FromHours(1))!;
        Assert.Equal(42UL, world.Cheapest[0].ListingId);
        Assert.Equal(PriceSource.LiveBoard, world.Source);
        Assert.Equal(2_997u, restored.LatestHome(49244, true, Now, TimeSpan.FromHours(1))!.LowestCompetitor);
        Assert.Equal(1, restored.SaleCount);
        Assert.Equal("Marilith", Assert.Single(restored.Candidates).CheapestWorld);
        Assert.Equal(Now, restored.CandidatesObservedAt);
    }

    [Fact]
    public void LoadingDropsDamagedEntriesInsteadOfFailing()
    {
        var data = new PriceMemoryData
        {
            Worlds = [null!, new WorldPriceSnapshot("", 1, true, PriceSource.LiveBoard, Now, 0, 0, []),
                new WorldPriceSnapshot("Exodus", 1, true, PriceSource.LiveBoard, Now, 1, 1, null!)],
            Sales = [null!, new RetainerSale(Now, 0, true, 1, 1, 0)],
        };
        var memory = new PriceMemory(data);
        Assert.Equal(1, memory.WorldSnapshotCount);
        Assert.Empty(memory.Latest("Exodus", 1, true, Now, TimeSpan.FromHours(1))!.Cheapest);
        Assert.Equal(0, memory.SaleCount);
    }
}

public sealed class RetainerSaleParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    private static Func<int, int?> Parameters(params int?[] values) =>
        index => index >= 0 && index < values.Length ? values[index] : null;

    [Fact]
    public void ReadsAStackSaleFromTheGameParameters()
    {
        // 745: lnum1 town, lnum2 item, lnum3 quantity, lnum5 gil after fees.
        var sale = RetainerSaleParser.Parse(745, Parameters(1, 1_049_240, 99, 0, 272_216), Now)!;
        Assert.Equal(49240u, sale.ItemId);
        Assert.True(sale.IsHighQuality);
        Assert.Equal(99u, sale.Quantity);
        Assert.Equal(272_216UL, sale.GilAfterFees);
        Assert.Equal(1u, sale.TownId);
        Assert.Equal(2_749.66m, decimal.Round(sale.NetUnitPrice, 2));
    }

    [Fact]
    public void ASingleItemSaleHasNoQuantityParameter()
    {
        var sale = RetainerSaleParser.Parse(748, Parameters(2, 13_708, null, null, 39_410), Now)!;
        Assert.Equal(13708u, sale.ItemId);
        Assert.False(sale.IsHighQuality);
        Assert.Equal(1u, sale.Quantity);
    }

    [Theory]
    [InlineData(6081u)]
    [InlineData(746u)]
    public void OtherMessagesAreNotSales(uint id) =>
        Assert.Null(RetainerSaleParser.Parse(id, Parameters(1, 49240, 99, 0, 1_000), Now));

    [Fact]
    public void MissingOrNonsenseParametersAreRejected()
    {
        Assert.Null(RetainerSaleParser.Parse(745, Parameters(1, null, 99, 0, 1_000), Now));
        Assert.Null(RetainerSaleParser.Parse(745, Parameters(1, 49240, 0, 0, 1_000), Now));
        Assert.Null(RetainerSaleParser.Parse(745, Parameters(1, 49240, 99, 0, -5), Now));
        Assert.Null(RetainerSaleParser.Parse(745, Parameters(1, 2_000_123, 99, 0, 1_000), Now));
    }

    [Theory]
    [InlineData(49240u, 49240u, false)]
    [InlineData(1_049_240u, 49240u, true)]
    [InlineData(500_123u, 123u, false)]
    [InlineData(2_000_001u, 0u, false)]
    public void SplitsQualityOffsets(uint raw, uint id, bool hq) =>
        Assert.Equal((id, hq), RetainerSaleParser.SplitItemId(raw));

    [Fact]
    public void QualityIsSettledFromTheMatchingListingWhenTheMessageLacksIt()
    {
        var sale = new RetainerSale(Now, 49240, false, 99, 247_500, 1);
        var listings = new (uint, bool, uint, uint)[]
        {
            (49240, false, 99, 1_000),
            (49240, true, 99, 2_632),
            (49240, true, 50, 5_000),
        };
        // 99 x 2,632 x 0.95 = 247,537: the HQ stack, within a few percent.
        Assert.True(RetainerSaleParser.ResolveQuality(sale, listings, 5m).IsHighQuality);
        // Nothing close enough: the parsed value stands.
        Assert.False(RetainerSaleParser.ResolveQuality(sale with { GilAfterFees = 100 }, listings, 5m).IsHighQuality);
    }
}
