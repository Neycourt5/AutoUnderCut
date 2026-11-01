namespace SmartUndercutBot.Core.Models;

/// <summary>
/// Where a remembered price came from. Only a live board reading describes stock
/// the bot actually saw; cached sources only ever decide where to look.
/// </summary>
public enum PriceSource
{
    LiveBoard = 0,
    Universalis = 1,
    Saddlebag = 2,
}

/// <summary>One remembered market-board row.</summary>
public sealed record RememberedListing(
    ulong ListingId,
    ulong RetainerId,
    uint PricePerUnit,
    uint Quantity);

/// <summary>
/// What one world's board showed for one item and quality at one moment: the
/// cheapest rows, plus how much of that quality was on the board in total.
/// </summary>
/// <param name="CompleteBelowPrice">
/// Zero when the whole board was read. A regional listing view only returns the
/// cheapest rows across every world, so for those snapshots every row priced below
/// this value is present and anything above it may be missing.
/// </param>
public sealed record WorldPriceSnapshot(
    string World,
    uint ItemId,
    bool IsHighQuality,
    PriceSource Source,
    DateTimeOffset ObservedAt,
    int Listings,
    ulong Units,
    RememberedListing[] Cheapest,
    uint CompleteBelowPrice = 0)
{
    public uint Lowest => Cheapest is { Length: > 0 } ? Cheapest[0].PricePerUnit : 0;

    /// <summary>Units remembered at or below a unit price.</summary>
    public ulong UnitsAtOrBelow(uint price) => Cheapest is null
        ? 0
        : Cheapest.Where(x => x.PricePerUnit <= price).Aggregate(0UL, (sum, x) => sum + x.Quantity);
}

/// <summary>What the home market looked like for one item and quality.</summary>
public sealed record HomeMarketSnapshot(
    uint ItemId,
    bool IsHighQuality,
    PriceSource Source,
    DateTimeOffset ObservedAt,
    uint LowestCompetitor,
    int CompetingListings,
    ulong CompetingUnits,
    decimal UnitsPerDay,
    decimal SalesPerDay,
    uint MedianSalePrice,
    uint MedianSaleQuantity);

/// <summary>One of our own completed retainer sales, as the game reported it.</summary>
public sealed record RetainerSale(
    DateTimeOffset At,
    uint ItemId,
    bool IsHighQuality,
    uint Quantity,
    ulong GilAfterFees,
    uint TownId,
    string ItemName = "")
{
    public decimal NetUnitPrice => Quantity == 0 ? 0m : GilAfterFees / (decimal)Quantity;
}

/// <summary>Our own sales of one item and quality over a window.</summary>
public sealed record SalesSummary(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Sales,
    ulong Units,
    ulong GilAfterFees,
    DateTimeOffset LastSaleAt);

/// <summary>
/// A market the discovery scan found worth watching. Intelligence only: nothing
/// here authorizes a purchase.
/// </summary>
/// <param name="HomePrice">A conservative home resale price: the lower of the recent median and the current floor.</param>
/// <param name="CheapestElsewhere">Cheapest cached listing on another world, or zero when unknown.</param>
/// <param name="Trend">The source's own label for recent price movement, such as "stable" or "crashing".</param>
public sealed record MarketCandidate(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    string Category,
    decimal UnitsPerDay,
    decimal SalesPerDay,
    uint HomePrice,
    uint CheapestElsewhere,
    string CheapestWorld,
    string Trend,
    PriceSource Source,
    DateTimeOffset ObservedAt)
{
    /// <summary>Net margin after both taxes if bought at the cheapest cached price and sold at the home price.</summary>
    public decimal? NetRoiPercent(decimal marketTaxPercent, decimal buyerFeePercent)
    {
        if (HomePrice == 0 || CheapestElsewhere == 0)
            return null;
        var landed = CheapestElsewhere * (1m + buyerFeePercent / 100m);
        var net = HomePrice * (1m - marketTaxPercent / 100m);
        return landed <= 0 ? null : (net - landed) / landed * 100m;
    }

    /// <summary>Gil the whole home market turns over per day at the home price.</summary>
    public decimal HomeGilPerDay => UnitsPerDay * HomePrice;

    /// <summary>Average units per sale, the stack size buyers actually take.</summary>
    public decimal UnitsPerSale => SalesPerDay <= 0 ? 0m : UnitsPerDay / SalesPerDay;
}

/// <summary>
/// A stretch of time the sale tracker was listening. Sales outside these spans
/// were never seen, so a share measured against all elapsed time would read low.
/// </summary>
public sealed record WatchedSpan(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>The persisted shape of the price memory.</summary>
public sealed class PriceMemoryData
{
    public int Version { get; set; } = 1;
    public List<WorldPriceSnapshot> Worlds { get; set; } = [];
    public List<HomeMarketSnapshot> Home { get; set; } = [];
    public List<RetainerSale> Sales { get; set; } = [];
    public List<MarketCandidate> Candidates { get; set; } = [];
    public DateTimeOffset? CandidatesObservedAt { get; set; }
    public List<WatchedSpan> Watched { get; set; } = [];
}
