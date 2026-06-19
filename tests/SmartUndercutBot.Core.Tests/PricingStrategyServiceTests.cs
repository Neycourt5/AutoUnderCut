using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class PricingStrategyServiceTests
{
    private readonly PricingStrategyService service = new();

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
        var decision = service.Evaluate(Context(current: 1_504, lowest: 1_500));

        Assert.Equal(PriceDecisionKind.WithinTolerance, decision.Kind);
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
        Assert.Equal(1_250u, decision.EffectiveFloor);
    }

    [Fact]
    public void DetectsPriceWarAgainstHistoricalMedian()
    {
        var rule = new PricingRule { PriceWarDropPercent = 20, AbsoluteTolerance = 0, PercentageTolerance = 0 };
        var decision = service.Evaluate(Context(current: 2_000, lowest: 700, historical: 1_000, rule: rule));

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
