using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class PricingStrategyServiceTests
{
    private readonly PricingStrategyService service = new();

    [Fact]
    public void RejectsSnapshotForADifferentItem()
    {
        var context = Context(current: 2_000, lowest: 1_500);
        var decision = service.Evaluate(context with { Market = context.Market with { ItemId = context.Listing.ItemId + 1 } });
        Assert.Equal(PriceDecisionKind.InvalidData, decision.Kind);
        Assert.False(decision.ShouldUpdate);
    }

    [Fact]
    public void EmptyListingCannotSetTheUndercutPrice()
    {
        var context = Context(current: 2_000, lowest: 1_500);
        var decision = service.Evaluate(context with
        {
            Market = context.Market with { Listings = [new(1, 0, false)] },
        });
        Assert.Equal(PriceDecisionKind.NoMarketData, decision.Kind);
    }

    [Fact]
    public void UndercutsLowestCompetitorByConfiguredAmount()
    {
        var decision = service.Evaluate(Context(current: 2_000, lowest: 1_500));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(1_499u, decision.TargetPrice);
    }

    [Fact]
    public void SkipsWhenInsideAbsoluteTolerance()
    {
        var rule = new PricingRule { AbsoluteTolerance = 5, PercentageTolerance = 0 };
        var decision = service.Evaluate(Context(current: 1_504, lowest: 1_500, rule: rule));

        Assert.Equal(PriceDecisionKind.WithinTolerance, decision.Kind);
    }

    [Fact]
    public void DefaultRuleAlwaysUndercutsEvenWhenCurrentPriceIsClose()
    {
        var decision = service.Evaluate(Context(current: 1_501, lowest: 1_500));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(1_499u, decision.TargetPrice);
    }

    [Fact]
    public void SkipsWhenAlreadyBelowLowestCompetitor()
    {
        var decision = service.Evaluate(Context(current: 1_400, lowest: 1_500));

        Assert.Equal(PriceDecisionKind.NoChange, decision.Kind);
        Assert.False(decision.ShouldUpdate);
    }

    [Fact]
    public void RefusesToCrossMinimumFloor()
    {
        var rule = new PricingRule { MinimumPrice = 1_600, AbsoluteTolerance = 0, PercentageTolerance = 0 };
        var decision = service.Evaluate(Context(current: 2_000, lowest: 1_500, rule: rule));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
    }

    [Fact]
    public void AppliesMarginToConfiguredCostBasis()
    {
        var rule = new PricingRule
        {
            CostBasis = 1_000,
            MinimumMarginPercent = 25,
            AbsoluteTolerance = 0,
            PercentageTolerance = 0,
        };
        var decision = service.Evaluate(Context(current: 2_000, lowest: 1_200, rule: rule));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.Equal(1_316u, decision.EffectiveFloor);
    }

    [Fact]
    public void NeverUndercutsBelowTrackedPurchaseCost()
    {
        var rule = new PricingRule
        {
            CostBasis = 1_050,
            MinimumMarginPercent = 0,
        };
        var decision = service.Evaluate(Context(current: 2_000, lowest: 900, rule: rule));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.Equal(1_107u, decision.EffectiveFloor);
        Assert.False(decision.ShouldUpdate);
    }

    [Theory]
    [InlineData(PriceWarAction.LeaveUnchanged)]
    [InlineData(PriceWarAction.MatchProtectedFloor)]
    public void PurchasedPotionsFollowProfitableMarketDespiteOldMarginAndLargeDrop(PriceWarAction action)
    {
        var rule = new PricingRule
        {
            CostBasis = 4_200,
            CostBasisUnits = 99,
            AcquisitionFloor = 5_300,
            MinimumMarginPercent = 20, // Previously ratcheted up automatically.
            PriceWarDropPercent = 20,
            PriceWarAction = action,
        };
        var decision = service.Evaluate(Context(current: 5_300, lowest: 4_700, historical: 7_000, rule: rule.Clone()));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(4_699u, decision.TargetPrice);
        Assert.Equal(4_423u, decision.EffectiveFloor);
        Assert.True(FeeModel.Default.NetUnitProceeds(decision.TargetPrice!.Value) > rule.CostBasis);
    }

    [Fact]
    public void PurchasedStockStillHonorsTheManualAbsoluteMinimum()
    {
        var rule = new PricingRule
        {
            CostBasis = 4_200, CostBasisUnits = 99, MinimumPrice = 4_800,
            ApplyMinimumPriceToPurchasedStock = true,
        };
        var decision = service.Evaluate(Context(current: 5_300, lowest: 4_700, historical: 5_300, rule: rule.Clone()));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.Equal(4_800u, decision.EffectiveFloor);
    }

    [Fact]
    public void SavedStrengthPotionRuleNoLongerTreatsTheOldAutomaticMinimumAsManual()
    {
        // Actual legacy rule: live listings stayed at 5,306 with competitors at
        // 4,749 because an old automatic minimum of 5,930 survived every update.
        var savedRule = System.Text.Json.JsonSerializer.Deserialize<PricingRule>("""
            { "CostBasis": 3681, "CostBasisUnits": 57, "MinimumPrice": 5930,
              "AcquisitionFloor": 4264, "MinimumMarginPercent": 20 }
            """)!;
        var config = new SmartUndercutBot.Configuration { PerItemRules = new() { [49234] = savedRule } };
        var listing = new RetainerListing(10, "MyRetainer", 0, 49234, "Strength potion", 57, 5306, true);
        var market = new MarketSnapshot(49234, DateTimeOffset.UtcNow, [new(4749, 99, true, "Other")], 5306);

        var decision = service.Evaluate(new(listing, market, config.GetEffectiveRule(49234), Fees: config.Fees));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(4_748u, decision.TargetPrice);
        Assert.Equal(3_876u, decision.EffectiveFloor);
        Assert.True(config.Fees.NetUnitProceeds(decision.TargetPrice!.Value) > savedRule.CostBasis);
    }

    [Fact]
    public void ManualCostRuleRetainsItsMinimumWithoutAnAdditionalOptIn()
    {
        var rule = new PricingRule { CostBasis = 3_681, MinimumPrice = 5_930 };
        var decision = service.Evaluate(Context(current: 5_306, lowest: 4_749, historical: 5_306, rule: rule));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.Equal(5_930u, decision.EffectiveFloor);
    }

    [Theory]
    [InlineData(4_423u, PriceDecisionKind.BelowFloor)]
    [InlineData(4_424u, PriceDecisionKind.Update)]
    public void RepricingRequiresMoreThanTheLandedCostAfterSellerTax(uint lowest, PriceDecisionKind expected)
    {
        var rule = new PricingRule { CostBasis = 4_200, CostBasisUnits = 99 };
        var decision = service.Evaluate(Context(current: 5_300, lowest: lowest, historical: 5_300, rule: rule));

        Assert.Equal(expected, decision.Kind);
        if (decision.TargetPrice is { } target)
            Assert.True(FeeModel.Default.NetUnitProceeds(target) > 4_200);
    }

    [Theory]
    [InlineData(3, PriceDecisionKind.Update)]
    [InlineData(5, PriceDecisionKind.BelowFloor)]
    public void RepricingUsesTheObservedSellerTax(int tax, PriceDecisionKind expected)
    {
        var rule = new PricingRule { CostBasis = 4_200, CostBasisUnits = 99 };
        var context = Context(current: 5_300, lowest: 4_400, historical: 5_300, rule: rule);
        var decision = service.Evaluate(context with { Fees = new FeeModel(tax) });

        Assert.Equal(expected, decision.Kind);
    }

    [Fact]
    public void RoundingStopsAtTheSmallestProfitablePrice()
    {
        var rule = new PricingRule
        {
            CostBasis = 4_200, CostBasisUnits = 99, Rounding = PriceRoundingMode.EndIn999,
        };
        var decision = service.Evaluate(Context(current: 5_300, lowest: 4_700, historical: 5_300, rule: rule));

        Assert.Equal(4_423u, decision.TargetPrice);
        Assert.Equal(4_201u, FeeModel.Default.NetUnitProceeds(decision.TargetPrice!.Value));
    }

    [Fact]
    public void ListingSpecificAcquisitionCostCannotBeUndercutUsingACheaperAverage()
    {
        var rule = new PricingRule { CostBasis = 4_200, CostBasisUnits = 99 };
        var context = Context(current: 5_300, lowest: 4_700, historical: 5_300, rule: rule);
        var decision = service.Evaluate(context with { Listing = context.Listing with { AcquisitionCost = 4_500 } });

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.Equal(4_738u, decision.EffectiveFloor);
    }

    [Fact]
    public void UnknownCostRetainsTheSavedFloor()
    {
        var rule = new PricingRule { AcquisitionFloor = 5_300 };
        var decision = service.Evaluate(Context(current: 5_300, lowest: 4_700, historical: 5_300, rule: rule));

        Assert.Equal(PriceDecisionKind.BelowFloor, decision.Kind);
        Assert.Equal(5_300u, decision.EffectiveFloor);
    }

    [Fact]
    public void ImpossibleProfitableFloorCannotBeClampedIntoALoss()
    {
        var rule = new PricingRule
        {
            CostBasis = PricingStrategyService.MaximumListingPrice,
            CostBasisUnits = 99,
            PriceWarAction = PriceWarAction.MatchProtectedFloor,
        };
        var decision = service.Evaluate(Context(current: PricingStrategyService.MaximumListingPrice,
            lowest: 100, historical: 10_000, rule: rule));

        Assert.Equal(PriceDecisionKind.InvalidData, decision.Kind);
        Assert.False(decision.ShouldUpdate);
    }

    [Fact]
    public void DetectsPriceWarAgainstHistoricalMedian()
    {
        var rule = new PricingRule { PriceWarDropPercent = 20, AbsoluteTolerance = 0, PercentageTolerance = 0 };
        var decision = service.Evaluate(Context(current: 2_000, lowest: 700, historical: 1_000, rule: rule));

        Assert.Equal(PriceDecisionKind.PriceWar, decision.Kind);
    }

    [Fact]
    public void DefaultGuardUndercutsOrdinaryTwentySixPercentMarketDrop()
    {
        var decision = service.Evaluate(Context(current: 5_696, lowest: 4_200, historical: 5_696));

        Assert.Equal(PriceDecisionKind.Update, decision.Kind);
        Assert.Equal(4_199u, decision.TargetPrice);
    }

    [Fact]
    public void DefaultGuardStillRejectsExtremeCrashListing()
    {
        var decision = service.Evaluate(Context(current: 5_696, lowest: 1_000, historical: 5_696));

        Assert.Equal(PriceDecisionKind.PriceWar, decision.Kind);
    }

    [Fact]
    public void ExcludesOwnRetainerFromCompetitors()
    {
        var listing = Listing(2_000);
        var market = new MarketSnapshot(1, DateTimeOffset.UtcNow,
            [new(500, 1, false, listing.RetainerName), new(1_500, 1, false, "SomeoneElse")], 1_500);
        var rule = new PricingRule { AbsoluteTolerance = 0, PercentageTolerance = 0 };

        var decision = service.Evaluate(new PricingContext(listing, market, rule));

        Assert.Equal(1_499u, decision.TargetPrice);
    }

    [Fact]
    public void ExcludesOtherOwnedRetainersFromCompetitors()
    {
        var listing = Listing(2_000);
        var market = new MarketSnapshot(1, DateTimeOffset.UtcNow,
            [new(500, 1, false, "MyOtherRetainer", 11), new(1_500, 1, false, "SomeoneElse", 99)], 1_500);
        var rule = new PricingRule();

        var decision = service.Evaluate(new PricingContext(listing, market, rule, new HashSet<ulong> { 10, 11 }));

        Assert.Equal(1_499u, decision.TargetPrice);
    }

    [Fact]
    public void RoundingNeverRaisesTheCompetitiveTarget()
    {
        var rule = new PricingRule
        {
            Rounding = PriceRoundingMode.EndIn99,
            AbsoluteTolerance = 0,
            PercentageTolerance = 0,
        };

        var decision = service.Evaluate(Context(current: 2_000, lowest: 1_551, rule: rule));

        Assert.Equal(1_499u, decision.TargetPrice);
    }

    private static PricingContext Context(uint current, uint lowest, uint historical = 1_500, PricingRule? rule = null) =>
        new(Listing(current), new MarketSnapshot(1, DateTimeOffset.UtcNow, [new(lowest, 1, false, "Other")], historical),
            rule ?? new PricingRule());

    private static RetainerListing Listing(uint current) =>
        new(10, "MyRetainer", 0, 1, "Test Item", 1, current, false);
}
