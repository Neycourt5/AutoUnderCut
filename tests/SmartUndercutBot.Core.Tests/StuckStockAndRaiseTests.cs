using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

/// <summary>
/// Decisions 1 and 2 of the 2026-11-01 review: stock the market has fallen below
/// goes up at break-even and, after a day, follows the market to a bounded loss;
/// stock cheaper than it needs to be moves up to just under the cheapest competitor.
/// </summary>
public sealed class StuckStockAndRaiseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly FeeModel Fees = new(5m, 5m);
    private readonly PricingStrategyService service = new();

    // The live Popcorn position on 2026-09-20: 2,142 landed, 2,256 break-even.
    private static PricingRule Popcorn() => new() { CostBasis = 2_142, CostBasisUnits = 2_929 };

    [Fact]
    public void TheFloorsComeFromTheLandedCostAndTheSaleTax()
    {
        Assert.Equal(2_256u, PositionCostPolicy.MinimumListingPrice(Popcorn(), Fees));
        // Proceeds of 95% of cost after the 5% sale tax: about the landed cost itself.
        Assert.Equal(2_143u, PositionCostPolicy.RelaxedListingPrice(Popcorn(), Fees, 0, 5m));
    }

    [Fact]
    public void AMarketBelowCostListsAtBreakEvenInsteadOfHoldingTheOldAsk()
    {
        var decision = Evaluate(Listing(2_400), Board(Competitor(2_190)), Popcorn());

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_256u, decision.TargetPrice);
        Assert.True(decision.MarketBelowCost);
    }

    [Fact]
    public void WithoutTheStuckStockPolicyTheOldAskIsHeld()
    {
        var decision = Evaluate(Listing(2_400), Board(Competitor(2_190)), Popcorn(), stuck: false);

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.False(decision.ShouldUpdate);
        Assert.True(decision.MarketBelowCost);
    }

    [Fact]
    public void AListingAlreadyAtBreakEvenStaysThereBeforeTheRelaxationPeriod()
    {
        var decision = Evaluate(Listing(2_256), Board(Competitor(2_190)), Popcorn(), since: Now.AddHours(-23));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.False(decision.ShouldUpdate);
    }

    [Fact]
    public void AfterADayBelowCostTheListingFollowsTheMarketDown()
    {
        var decision = Evaluate(Listing(2_256), Board(Competitor(2_190)), Popcorn(), since: Now.AddHours(-24));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_189u, decision.TargetPrice);
        Assert.Equal(2_143u, decision.EffectiveFloor);
    }

    [Fact]
    public void TheLossIsBoundedAtFivePercentUnderCost()
    {
        var decision = Evaluate(Listing(2_256), Board(Competitor(1_900)), Popcorn(), since: Now.AddHours(-30));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_143u, decision.TargetPrice);
    }

    [Fact]
    public void ACrashIsNeverFollowedBelowCostHoweverLongTheStockHasWaited()
    {
        // 60% under a 2,400 median is a crash, not a market.
        var decision = Evaluate(Listing(2_256), Board(Competitor(900)), Popcorn(), since: Now.AddHours(-72));

        Assert.Equal(PriceDecisionKind.PriceWar, decision.Kind);
        Assert.False(decision.ShouldUpdate);
        Assert.True(decision.MarketBelowCost);
    }

    [Fact]
    public void AMinimumTheUserSetByHandIsNeverRelaxed()
    {
        var rule = Popcorn();
        rule.ApplyMinimumPriceToPurchasedStock = true;
        rule.MinimumPrice = 2_300;

        var decision = Evaluate(Listing(2_400), Board(Competitor(2_190)), rule, since: Now.AddHours(-48));

        Assert.Equal(2_300u, decision.TargetPrice);
    }

    [Fact]
    public void StockOfUnknownCostHasNothingToRelax()
    {
        var rule = new PricingRule { MinimumPrice = 500 };
        Assert.Equal(500u, PositionCostPolicy.RelaxedListingPrice(rule, Fees, 0, 5m));
    }

    [Fact]
    public void ACheapListingIsRaisedToJustUnderTheCheapestCompetitor()
    {
        var decision = Evaluate(Listing(2_098), Board(Competitor(2_500)), new PricingRule());

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_499u, decision.TargetPrice);
    }

    [Fact]
    public void ARaiseStopsAtTheMedianCap()
    {
        var decision = Evaluate(Listing(2_098), Board(Competitor(3_500)), new PricingRule());

        // 110% of the 2,400 HQ median.
        Assert.Equal(2_640u, decision.TargetPrice);
    }

    [Fact]
    public void NoSameQualityHistoryMeansNoRaise()
    {
        // Only NQ sales arrived: they say nothing about what HQ is worth.
        var board = Board(Competitor(2_500)) with { HistoricalMedianHighQuality = null, HistoricalMedianNormalQuality = 1_200 };

        var decision = Evaluate(Listing(2_098), board, new PricingRule());

        Assert.Equal(PriceDecisionKind.NoChange, decision.Kind);
        Assert.False(decision.ShouldUpdate);
    }

    [Fact]
    public void ATinyRaiseIsNotWorthAPriceWrite()
    {
        var decision = Evaluate(Listing(2_490), Board(Competitor(2_500)), new PricingRule());

        Assert.Equal(PriceDecisionKind.NoChange, decision.Kind);
    }

    [Fact]
    public void WithoutTheRaisePolicyACheapListingIsLeftAlone()
    {
        var decision = Evaluate(Listing(2_098), Board(Competitor(2_500)), new PricingRule(), raise: false);

        Assert.Equal(PriceDecisionKind.NoChange, decision.Kind);
    }

    [Fact]
    public void ATieWithACompetitorIsUndercut()
    {
        var decision = Evaluate(Listing(2_500), Board(Competitor(2_500)), new PricingRule());

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_499u, decision.TargetPrice);
    }

    [Fact]
    public void AListingAlreadyCheapestWithinTheUndercutIsNotLoweredFurther()
    {
        var rule = new PricingRule { UndercutAmount = 5 };

        var decision = Evaluate(Listing(2_497), Board(Competitor(2_500)), rule);

        Assert.Equal(PriceDecisionKind.NoChange, decision.Kind);
    }

    [Fact]
    public void TheRaiseUsesTheTrueCheapestCompetitorNotTheDepthAdjustedPrice()
    {
        // Five cheap units at 2,200 sit in front of a deep wall at 2,600. Raising to
        // the wall would give up first place to them.
        var board = Board(Competitor(2_200, 5), Competitor(2_600, 200)) with { AbsorbableUnits = 50 };

        var decision = Evaluate(Listing(2_000), board, new PricingRule());

        Assert.Equal(2_199u, decision.TargetPrice);
    }

    [Fact]
    public void AListingUnderItsCostFloorIsRaisedToBreakEvenNotToALoss()
    {
        var decision = Evaluate(Listing(2_100), Board(Competitor(2_190)), Popcorn());

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_256u, decision.TargetPrice);
    }

    [Fact]
    public void NothingIsRaisedIntoACrash()
    {
        // Known-cost stock far above its floor skips the first crash check, so the
        // raise has its own.
        var rule = new PricingRule { CostBasis = 100, CostBasisUnits = 99 };

        var decision = Evaluate(Listing(800), Board(Competitor(900)), rule);

        Assert.False(decision.ShouldUpdate);
    }

    [Fact]
    public void TheOnlySellerIsRaisedToTheMedianCap()
    {
        var own = Enumerable.Range(0, 19).Select(i => Own(2_098)).ToArray();

        var decision = Evaluate(Listing(2_098), Board(own), new PricingRule());

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(2_640u, decision.TargetPrice);
    }

    [Fact]
    public void ABoardThatEndsOnAPageBoundaryMayHideADearerCompetitor()
    {
        var own = Enumerable.Range(0, 20).Select(i => Own(2_098)).ToArray();

        var decision = Evaluate(Listing(2_098), Board(own), new PricingRule());

        Assert.Equal(PriceDecisionKind.NoMarketData, decision.Kind);
    }

    [Fact]
    public void AnEmptyBoardRaisesNothing()
    {
        var decision = Evaluate(Listing(2_098), Board(), new PricingRule());

        Assert.Equal(PriceDecisionKind.NoMarketData, decision.Kind);
    }

    [Fact]
    public void ANewBagStackOfStuckStockGoesUpAtBreakEven()
    {
        // The bag-listing placeholder sits at the game's maximum until priced.
        var decision = Evaluate(Listing(PricingStrategyService.MaximumListingPrice), Board(Competitor(2_190)), Popcorn());

        Assert.Equal(2_256u, decision.TargetPrice);
    }

    private PriceDecision Evaluate(RetainerListing listing, MarketSnapshot board, PricingRule rule,
        DateTimeOffset? since = null, bool stuck = true, bool raise = true) =>
        service.Evaluate(new(listing, board, rule, new HashSet<ulong> { 10 }, Fees,
            stuck ? StuckStockPolicy.Default : null, since, Now, raise ? PriceRaisePolicy.Default : null));

    private static RetainerListing Listing(uint price) => new(10, "Mine", 0, 1, "Caramel Popcorn", 50, price, true);

    private static MarketListing Competitor(uint price, uint quantity = 50) => new(price, quantity, true, "Other", 99);

    private static MarketListing Own(uint price) => new(price, 50, true, "Mine", 10);

    private static MarketSnapshot Board(params MarketListing[] rows) =>
        new(1, Now, rows, 2_400, HistoricalMedianHighQuality: 2_400, HistoricalMedianNormalQuality: 1_200);
}
